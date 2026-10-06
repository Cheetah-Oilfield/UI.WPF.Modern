using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using iNKORE.UI.WPF.Modern.Mcp.Index;

namespace iNKORE.UI.WPF.Modern.Mcp.IndexGen;

/// <summary>
/// Reads the public, XAML-relevant API surface of the library assemblies through
/// <see cref="MetadataLoadContext"/>, so WPF is never loaded into this process.
/// </summary>
internal sealed class AssemblyScanner : IDisposable
{
    private const string LibraryNamespace = "iNKORE.UI.WPF.Modern";

    private readonly MetadataLoadContext _context;
    private readonly XmlDocReader _docs;

    private readonly Type? _control;
    private readonly Type? _frameworkElement;
    private readonly Type? _frameworkContentElement;
    private readonly Type? _dependencyObject;
    private readonly Type? _dependencyProperty;
    private readonly Type? _routedEvent;
    private readonly Type? _markupExtension;
    private readonly Type? _resourceDictionary;
    private readonly Type? _valueConverter;
    private readonly Type? _multiValueConverter;

    public AssemblyScanner(IEnumerable<string> resolverPaths, XmlDocReader docs)
    {
        _docs = docs;
        _context = new MetadataLoadContext(new PathAssemblyResolver(resolverPaths));

        _control = FindType("PresentationFramework", "System.Windows.Controls.Control");
        _frameworkElement = FindType("PresentationFramework", "System.Windows.FrameworkElement");
        _frameworkContentElement = FindType("PresentationFramework", "System.Windows.FrameworkContentElement");
        _resourceDictionary = FindType("PresentationFramework", "System.Windows.ResourceDictionary");
        _valueConverter = FindType("PresentationFramework", "System.Windows.Data.IValueConverter");
        _multiValueConverter = FindType("PresentationFramework", "System.Windows.Data.IMultiValueConverter");
        _dependencyObject = FindType("WindowsBase", "System.Windows.DependencyObject");
        _dependencyProperty = FindType("WindowsBase", "System.Windows.DependencyProperty");
        _markupExtension = FindType("System.Xaml", "System.Windows.Markup.MarkupExtension");
        _routedEvent = FindType("PresentationCore", "System.Windows.RoutedEvent");
    }

    public void Dispose() => _context.Dispose();

    /// <summary>
    /// Builds the resolver path list: the library output folders, the .NET and WindowsDesktop
    /// shared runtimes, and the NuGet dependencies recorded in the project's assets file.
    /// </summary>
    public static List<string> BuildResolverPaths(IEnumerable<string> libraryDirectories, string assetsFile, string targetFramework)
    {
        var paths = new List<string>();
        foreach (var dir in libraryDirectories)
            paths.AddRange(Directory.GetFiles(dir, "*.dll"));

        var netCoreDir = RuntimeEnvironment.GetRuntimeDirectory();
        paths.AddRange(Directory.GetFiles(netCoreDir, "*.dll"));

        var desktopDir = FindWindowsDesktopRuntime(netCoreDir)
            ?? throw new InvalidOperationException("The Microsoft.WindowsDesktop.App runtime is required to read WPF metadata.");
        paths.AddRange(Directory.GetFiles(desktopDir, "*.dll"));

        paths.AddRange(ReadPackageAssemblies(assetsFile, targetFramework));
        return paths;
    }

    public (string Version, string XmlNamespace, List<string> ClrNamespaces) ReadAssemblyInfo(Assembly assembly)
    {
        var version = assembly.GetName().Version ?? new Version(0, 0, 0);
        string? uri = null;
        var namespaces = new List<string>();

        foreach (var attribute in assembly.GetCustomAttributesData())
        {
            if (attribute.AttributeType.Name != "XmlnsDefinitionAttribute")
                continue;

            uri ??= attribute.ConstructorArguments[0].Value as string;
            if (attribute.ConstructorArguments[1].Value is string clr)
                namespaces.Add(clr);
        }

        return ($"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}", uri ?? "", namespaces);
    }

    public Assembly Load(string path) => _context.LoadFromAssemblyPath(path);

    public IEnumerable<TypeEntry> Scan(Assembly assembly, ISet<string> xmlNamespaceClrNamespaces)
    {
        Type[] types;
        try
        {
            types = assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).ToArray()!;
        }

        foreach (var type in types.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            var entry = TryCreateEntry(type, assembly, xmlNamespaceClrNamespaces);
            if (entry is not null)
                yield return entry;
        }
    }

    /// <summary>
    /// Creates a members-less entry for a built-in WPF type (e.g. Button) that the library restyles,
    /// so its styles, samples and docs can hang off it.
    /// </summary>
    public TypeEntry? TryCreateWpfEntry(string name)
    {
        foreach (var assemblyName in new[] { "PresentationFramework", "PresentationCore", "WindowsBase" })
        {
            Type? type;
            try
            {
                type = _context.LoadFromAssemblyName(assemblyName).GetExportedTypes()
                    .FirstOrDefault(t => t.Name == name && t.Namespace?.StartsWith("System.Windows", StringComparison.Ordinal) == true);
            }
            catch (FileNotFoundException)
            {
                continue;
            }

            if (type is null)
                continue;

            return new TypeEntry
            {
                Name = FriendlyName(type),
                FullName = DocId(type),
                Namespace = type.Namespace!,
                Assembly = assemblyName,
                Kind = Classify(type, hasAttachedProperties: false) ?? TypeKind.Object,
                BaseType = type.BaseType is null ? null : FriendlyName(type.BaseType),
                IsAbstract = type.IsAbstract && !type.IsSealed,
            };
        }

        return null;
    }

    private TypeEntry? TryCreateEntry(Type type, Assembly assembly, ISet<string> xmlNamespaceClrNamespaces)
    {
        var ns = type.Namespace ?? "";
        if (!ns.StartsWith(LibraryNamespace, StringComparison.Ordinal) || ns.Contains(".Native", StringComparison.Ordinal))
            return null;
        if (type.Name.StartsWith('<'))
            return null;

        List<PropertyEntry> attached;
        TypeKind? kind;
        try
        {
            attached = type.IsEnum ? [] : ReadAttachedProperties(type);
            kind = Classify(type, attached.Count > 0);
        }
        catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException)
        {
            Console.Error.WriteLine($"warning: skipped {type.FullName}: {ex.Message}");
            return null;
        }

        if (kind is null)
            return null;

        var docId = DocId(type);
        var obsolete = FindAttribute(type, "System.ObsoleteAttribute");
        var entry = new TypeEntry
        {
            Name = FriendlyName(type),
            FullName = docId,
            Namespace = ns,
            Assembly = assembly.GetName().Name!,
            Kind = kind.Value,
            BaseType = type.IsEnum || type.BaseType is null ? null : FriendlyName(type.BaseType),
            IsAbstract = type.IsAbstract && !type.IsSealed,
            IsObsolete = obsolete is not null,
            ObsoleteMessage = obsolete?.ConstructorArguments.FirstOrDefault().Value as string,
            Summary = _docs.Type(docId),
            InXmlNamespace = xmlNamespaceClrNamespaces.Contains(ns),
            ContentProperty = FindAttribute(type, "System.Windows.Markup.ContentPropertyAttribute")?.ConstructorArguments.FirstOrDefault().Value as string,
        };

        if (type.IsEnum)
        {
            entry.EnumValues = ReadEnumValues(type, docId);
            return entry;
        }

        var properties = ReadProperties(type, docId);
        foreach (var a in attached)
            a.Summary = _docs.Method(docId, "Get" + a.Name) ?? _docs.Field(docId, a.Name + "Property");
        properties.AddRange(attached);

        entry.Properties = NullIfEmpty(properties);
        entry.Events = NullIfEmpty(ReadEvents(type, docId));
        entry.TemplateParts = NullIfEmpty(type.GetCustomAttributesData()
            .Where(a => a.AttributeType.FullName == "System.Windows.TemplatePartAttribute")
            .Select(a => new TemplatePartEntry
            {
                Name = NamedArgument(a, "Name") as string ?? "",
                Type = NamedArgument(a, "Type") is Type t ? FriendlyName(t) : null,
            })
            .Where(p => p.Name.Length > 0)
            .ToList());
        entry.VisualStates = NullIfEmpty(type.GetCustomAttributesData()
            .Where(a => a.AttributeType.FullName == "System.Windows.TemplateVisualStateAttribute")
            .Select(a => new VisualStateEntry
            {
                Name = NamedArgument(a, "Name") as string ?? "",
                GroupName = NamedArgument(a, "GroupName") as string,
            })
            .Where(s => s.Name.Length > 0)
            .ToList());

        return entry;
    }

    private TypeKind? Classify(Type type, bool hasAttachedProperties)
    {
        if (type.IsEnum)
            return TypeKind.Enum;
        if (!type.IsClass)
            return null;
        if (IsSubclassOf(type, _markupExtension))
            return TypeKind.MarkupExtension;
        if (IsSubclassOf(type, _resourceDictionary))
            return TypeKind.ResourceDictionary;
        if (Implements(type, _valueConverter) || Implements(type, _multiValueConverter))
            return TypeKind.Converter;
        if (IsSubclassOf(type, _control))
            return TypeKind.Control;
        if (IsSubclassOf(type, _frameworkElement) || IsSubclassOf(type, _frameworkContentElement))
            return TypeKind.Element;
        if (hasAttachedProperties)
            return TypeKind.Helper;
        if (IsSubclassOf(type, _dependencyObject) && !type.IsAbstract)
            return TypeKind.Object;
        return null;
    }

    private List<PropertyEntry> ReadProperties(Type type, string docId)
    {
        var dependencyProperties = StaticFieldNames(type, _dependencyProperty, "Property");
        var result = new List<PropertyEntry>();

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (property.GetIndexParameters().Length > 0 || property.GetMethod is not { IsPublic: true })
                continue;

            result.Add(new PropertyEntry
            {
                Name = property.Name,
                Type = FriendlyName(property.PropertyType),
                Summary = _docs.Property(docId, property.Name) ?? _docs.Field(docId, property.Name + "Property"),
                IsDependencyProperty = dependencyProperties.Contains(property.Name),
                IsReadOnly = property.SetMethod is not { IsPublic: true },
                IsObsolete = FindAttribute(property, "System.ObsoleteAttribute") is not null,
            });
        }

        return result.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    private List<PropertyEntry> ReadAttachedProperties(Type type)
    {
        var dependencyProperties = StaticFieldNames(type, _dependencyProperty, "Property");
        if (dependencyProperties.Count == 0)
            return [];

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        var setters = methods
            .Where(m => m.Name.StartsWith("Set", StringComparison.Ordinal) && m.GetParameters().Length == 2)
            .Select(m => m.Name[3..])
            .ToHashSet(StringComparer.Ordinal);

        var result = new List<PropertyEntry>();
        foreach (var getter in methods)
        {
            if (!getter.Name.StartsWith("Get", StringComparison.Ordinal) || getter.GetParameters().Length != 1)
                continue;

            var name = getter.Name[3..];
            if (!dependencyProperties.Contains(name) || result.Any(r => r.Name == name))
                continue;

            result.Add(new PropertyEntry
            {
                Name = name,
                Type = FriendlyName(getter.ReturnType),
                IsDependencyProperty = true,
                IsAttached = true,
                IsReadOnly = !setters.Contains(name),
                AttachedTo = FriendlyName(getter.GetParameters()[0].ParameterType),
                IsObsolete = FindAttribute(getter, "System.ObsoleteAttribute") is not null,
            });
        }

        return result.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    private List<EventEntry> ReadEvents(Type type, string docId)
    {
        var routedEvents = StaticFieldNames(type, _routedEvent, "Event");
        return type.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(e => new EventEntry
            {
                Name = e.Name,
                Type = e.EventHandlerType is null ? "EventHandler" : FriendlyName(e.EventHandlerType),
                Summary = _docs.Event(docId, e.Name) ?? _docs.Field(docId, e.Name + "Event"),
                IsRouted = routedEvents.Contains(e.Name),
            })
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
    }

    private List<EnumValueEntry> ReadEnumValues(Type type, string docId) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => new EnumValueEntry
            {
                Name = f.Name,
                Value = Convert.ToInt64(f.GetRawConstantValue()),
                Summary = _docs.Field(docId, f.Name),
            })
            .OrderBy(v => v.Value)
            .ToList();

    private static HashSet<string> StaticFieldNames(Type type, Type? fieldType, string suffix)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (fieldType is null)
            return names;

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (field.FieldType == fieldType && field.Name.EndsWith(suffix, StringComparison.Ordinal))
                names.Add(field.Name[..^suffix.Length]);
        }

        return names;
    }

    private static bool IsSubclassOf(Type type, Type? baseType)
    {
        if (baseType is null)
            return false;
        try
        {
            return type.IsSubclassOf(baseType);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    private static bool Implements(Type type, Type? interfaceType)
    {
        if (interfaceType is null)
            return false;
        try
        {
            return type.GetInterfaces().Contains(interfaceType);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    private static CustomAttributeData? FindAttribute(MemberInfo member, string fullName)
    {
        try
        {
            return member.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.FullName == fullName);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static object? NamedArgument(CustomAttributeData attribute, string name)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.MemberName == name)
                return argument.TypedValue.Value;
        }

        return null;
    }

    private Type? FindType(string assemblyName, string typeName)
    {
        try
        {
            return _context.LoadFromAssemblyName(assemblyName).GetType(typeName);
        }
        catch (FileNotFoundException)
        {
            Console.Error.WriteLine($"warning: could not resolve {assemblyName} for {typeName}");
            return null;
        }
    }

    private static string DocId(Type type) => (type.FullName ?? type.Name).Replace('+', '.');

    private static readonly Dictionary<string, string> Keywords = new(StringComparer.Ordinal)
    {
        ["System.Boolean"] = "bool",
        ["System.Byte"] = "byte",
        ["System.Char"] = "char",
        ["System.Decimal"] = "decimal",
        ["System.Double"] = "double",
        ["System.Int16"] = "short",
        ["System.Int32"] = "int",
        ["System.Int64"] = "long",
        ["System.Object"] = "object",
        ["System.Single"] = "float",
        ["System.String"] = "string",
        ["System.UInt32"] = "uint",
        ["System.Void"] = "void",
    };

    internal static string FriendlyName(Type type)
    {
        if (type.IsArray)
            return FriendlyName(type.GetElementType()!) + "[]";
        if (type.IsByRef)
            return FriendlyName(type.GetElementType()!);
        if (type.FullName is { } fullName && Keywords.TryGetValue(fullName, out var keyword))
            return keyword;

        var prefix = type.IsNested && !type.IsGenericParameter ? FriendlyName(type.DeclaringType!) + "." : "";

        if (!type.IsGenericType)
            return prefix + type.Name;

        var args = type.GetGenericArguments();
        if (type.GetGenericTypeDefinition().FullName == "System.Nullable`1")
            return FriendlyName(args[0]) + "?";

        var name = type.Name;
        var tick = name.IndexOf('`');
        if (tick >= 0)
            name = name[..tick];
        return $"{prefix}{name}<{string.Join(", ", args.Select(FriendlyName))}>";
    }

    private static List<T>? NullIfEmpty<T>(List<T> list) => list.Count == 0 ? null : list;

    private static string? FindWindowsDesktopRuntime(string netCoreDir)
    {
        var netCore = new DirectoryInfo(netCoreDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var desktopRoot = Path.Combine(netCore.Parent!.Parent!.FullName, "Microsoft.WindowsDesktop.App");
        if (!Directory.Exists(desktopRoot))
            return null;

        var sameVersion = Path.Combine(desktopRoot, netCore.Name);
        if (Directory.Exists(sameVersion))
            return sameVersion;

        return Directory.GetDirectories(desktopRoot)
            .Select(d => (Dir: d, Version: Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) ? v : null))
            .Where(x => x.Version is not null)
            .OrderByDescending(x => x.Version)
            .Select(x => x.Dir)
            .FirstOrDefault();
    }

    private static IEnumerable<string> ReadPackageAssemblies(string assetsFile, string targetFramework)
    {
        if (!File.Exists(assetsFile))
        {
            Console.Error.WriteLine($"warning: assets file not found, package dependencies may not resolve: {assetsFile}");
            yield break;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(assetsFile));
        var root = doc.RootElement;
        var packageFolders = root.GetProperty("packageFolders").EnumerateObject().Select(p => p.Name).ToList();
        var libraries = root.GetProperty("libraries");

        // Target keys look like "net6.0-windows10.0.18362" (no RID).
        var frameworkPrefix = targetFramework.Split('-')[0];
        foreach (var target in root.GetProperty("targets").EnumerateObject())
        {
            if (target.Name.Contains('/') || !target.Name.StartsWith(frameworkPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var package in target.Value.EnumerateObject())
            {
                if (package.Value.GetProperty("type").GetString() != "package")
                    continue;
                if (!libraries.TryGetProperty(package.Name, out var library) || !library.TryGetProperty("path", out var libraryPath))
                    continue;

                foreach (var group in new[] { "runtime", "compile" })
                {
                    if (!package.Value.TryGetProperty(group, out var files))
                        continue;

                    foreach (var file in files.EnumerateObject())
                    {
                        if (!file.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                            continue;

                        foreach (var folder in packageFolders)
                        {
                            var full = Path.Combine(folder, libraryPath.GetString()!, file.Name);
                            if (File.Exists(full))
                            {
                                yield return full;
                                break;
                            }
                        }
                    }
                }
            }
        }
    }
}
