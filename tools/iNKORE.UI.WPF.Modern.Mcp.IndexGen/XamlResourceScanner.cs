using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using iNKORE.UI.WPF.Modern.Mcp.Index;

namespace iNKORE.UI.WPF.Modern.Mcp.IndexGen;

/// <summary>
/// Extracts resource keys, per-theme values, styles and the keys each style references from the
/// library's XAML resource dictionaries.
/// </summary>
internal sealed partial class XamlResourceScanner
{
    public static readonly string[] ThemeNames = ["Light", "Dark", "HighContrast"];

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private const int MaxXamlLength = 1200;

    private readonly string _repoRoot;
    private readonly Dictionary<string, ResourceEntry> _resources = new(StringComparer.Ordinal);

    // Raw per-theme definitions, before alias resolution: theme -> key -> definition.
    private readonly Dictionary<string, Dictionary<string, RawValue>> _themeValues = new(StringComparer.Ordinal);

    // Raw theme-independent definitions: key -> definition.
    private readonly Dictionary<string, RawValue> _globalValues = new(StringComparer.Ordinal);

    // ThemeKeys member name (e.g. "AccentButtonStyleKey") -> resource key (e.g. "AccentButtonStyle").
    private readonly Dictionary<string, string> _codeKeyToKey = new(StringComparer.Ordinal);

    public XamlResourceScanner(string repoRoot) => _repoRoot = repoRoot;

    public IReadOnlyCollection<ResourceEntry> Resources => _resources.Values;

    public void ReadThemeKeys(IEnumerable<string> themeKeysFiles)
    {
        foreach (var file in themeKeysFiles)
        {
            foreach (Match match in ThemeKeyFieldRegex().Matches(File.ReadAllText(file)))
                _codeKeyToKey.TryAdd(match.Groups[1].Value, match.Groups[2].Value);
        }
    }

    public void ScanThemeScheme(string theme, string path)
    {
        var values = new Dictionary<string, RawValue>(StringComparer.Ordinal);
        _themeValues[theme] = values;

        foreach (var (key, element, isImplicit) in TopLevelResources(path))
        {
            var raw = ReadRaw(element);
            values.TryAdd(key, raw);

            var entry = GetOrAdd(key, element, path, isImplicit);
            entry.IsThemeVarying = true;
        }
    }

    public void ScanDictionary(string path)
    {
        foreach (var (key, element, isImplicit) in TopLevelResources(path))
        {
            _globalValues.TryAdd(key, ReadRaw(element));
            GetOrAdd(key, element, path, isImplicit);
        }
    }

    /// <summary>Resolves values, links ThemeKeys accessors and builds the reverse "used by" maps.</summary>
    public List<ResourceEntry> Build()
    {
        var codeKeys = _codeKeyToKey
            .GroupBy(kv => kv.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => "ThemeKeys." + g.First().Key, StringComparer.Ordinal);

        foreach (var entry in _resources.Values)
        {
            entry.CodeKey = codeKeys.GetValueOrDefault(entry.Key);

            if (entry.IsThemeVarying)
            {
                entry.Themes = new Dictionary<string, ThemeValue>(StringComparer.Ordinal);
                foreach (var theme in ThemeNames)
                {
                    if (_themeValues.TryGetValue(theme, out var values) && values.TryGetValue(entry.Key, out var raw))
                        entry.Themes[theme] = Resolve(raw, theme);
                }
            }
            else if (_globalValues.TryGetValue(entry.Key, out var raw))
            {
                entry.Value = Resolve(raw, theme: null);
            }

            if (entry.References is { Count: > 0 })
                entry.References = entry.References.Select(MapCodeKey).Distinct(StringComparer.Ordinal).ToList();
        }

        foreach (var entry in _resources.Values)
        {
            var alias = entry.Themes?.Values.Select(v => v.AliasOf).FirstOrDefault(a => a is not null) ?? entry.Value?.AliasOf;
            if (alias is not null && _resources.TryGetValue(alias, out var target))
                (target.AliasedBy ??= []).Add(entry.Key);

            if (entry.References is null)
                continue;

            foreach (var reference in entry.References)
            {
                if (!_resources.TryGetValue(reference, out var used))
                    continue;

                (used.UsedByStyles ??= []).Add(entry.Key);
                if (entry.TargetType is not null)
                    (used.UsedByTypes ??= []).Add(entry.TargetType);
            }
        }

        foreach (var entry in _resources.Values)
        {
            if (entry.ValueType is "StaticResource" or "DynamicResource")
                entry.ValueType = AliasTargetType(entry) ?? entry.ValueType;

            entry.AliasedBy = Sorted(entry.AliasedBy);
            entry.UsedByStyles = Sorted(entry.UsedByStyles);
            entry.UsedByTypes = Sorted(entry.UsedByTypes);
        }

        return _resources.Values.OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
    }

    private string? AliasTargetType(ResourceEntry entry)
    {
        var current = entry;
        for (var depth = 0; depth < 16; depth++)
        {
            var alias = current.Themes?.Values.Select(v => v.AliasOf).FirstOrDefault(a => a is not null) ?? current.Value?.AliasOf;
            if (alias is null || !_resources.TryGetValue(alias, out var next))
                return null;
            if (next.ValueType is not ("StaticResource" or "DynamicResource"))
                return next.ValueType;
            current = next;
        }

        return null;
    }

    private string MapCodeKey(string reference)
    {
        const string prefix = "ThemeKeys.";
        if (reference.StartsWith(prefix, StringComparison.Ordinal)
            && _codeKeyToKey.TryGetValue(reference[prefix.Length..], out var key))
        {
            return key;
        }

        return reference;
    }

    private ResourceEntry GetOrAdd(string key, XElement element, string path, bool isImplicit)
    {
        var relative = Path.GetRelativePath(_repoRoot, path).Replace('\\', '/');
        if (_resources.TryGetValue(key, out var existing))
        {
            if (!existing.DefinedIn.Contains(relative))
                existing.DefinedIn.Add(relative);
            return existing;
        }

        var entry = new ResourceEntry
        {
            Key = key,
            ValueType = element.Name.LocalName,
            DefinedIn = [relative],
            IsImplicitStyle = isImplicit,
        };

        if (element.Name.LocalName is "Style" or "ControlTemplate" or "DataTemplate")
        {
            entry.TargetType = NormalizeTypeName((string?)element.Attribute("TargetType") ?? (string?)element.Attribute("DataType"));
            entry.BasedOn = ExtractReferences((string?)element.Attribute("BasedOn") ?? "").FirstOrDefault()
                ?? ExtractTypeReference((string?)element.Attribute("BasedOn"));
            var references = CollectReferences(element);
            entry.References = references.Count == 0 ? null : references;
        }

        _resources.Add(key, entry);
        return entry;
    }

    private IEnumerable<(string Key, XElement Element, bool IsImplicit)> TopLevelResources(string path)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: could not parse {path}: {ex.Message}");
            yield break;
        }

        if (doc.Root is not { Name.LocalName: "ResourceDictionary" } root)
            yield break;

        foreach (var element in root.Elements())
        {
            // Property elements such as ResourceDictionary.MergedDictionaries.
            if (element.Name.LocalName.Contains('.'))
                continue;

            var key = (string?)element.Attribute(X + "Key");
            if (key is not null)
            {
                yield return (NormalizeKey(key), element, false);
                continue;
            }

            if (element.Name.LocalName is "Style" or "DataTemplate" or "ControlTemplate"
                && NormalizeTypeName((string?)element.Attribute("TargetType") ?? (string?)element.Attribute("DataType")) is { } target)
            {
                yield return ($"{{x:Type {target}}}", element, true);
            }
        }
    }

    private RawValue ReadRaw(XElement element)
    {
        var type = element.Name.LocalName;

        if (type is "StaticResource" or "DynamicResource" && (string?)element.Attribute("ResourceKey") is { } aliasOf)
            return new RawValue(type, AliasOf: NormalizeKey(aliasOf));

        if (type == "SolidColorBrush")
        {
            var color = (string?)element.Attribute("Color") ?? element.Elements().FirstOrDefault(e => e.Name.LocalName.EndsWith(".Color", StringComparison.Ordinal))?.Value.Trim();
            var opacity = (string?)element.Attribute("Opacity");
            if (color is not null && ExtractReferences(color).FirstOrDefault() is { } colorKey)
                return new RawValue(type, References: colorKey, Opacity: opacity);
            return new RawValue(type, Literal: color, Opacity: opacity);
        }

        if (!element.HasElements && element.Value.Trim() is { Length: > 0 } text)
            return new RawValue(type, Literal: EscapeText(text));

        return new RawValue(type, Xaml: Snippet(element));
    }

    private ThemeValue Resolve(RawValue raw, string? theme)
    {
        if (raw.AliasOf is not null)
            return new ThemeValue { AliasOf = raw.AliasOf, Value = ResolveLiteral(raw.AliasOf, theme, depth: 0) };

        if (raw.References is not null)
        {
            var color = ResolveLiteral(raw.References, theme, depth: 0);
            return new ThemeValue
            {
                References = raw.References,
                Value = color is null ? null : WithOpacity(color, raw.Opacity),
            };
        }

        return new ThemeValue
        {
            Value = raw.Literal is null ? null : WithOpacity(raw.Literal, raw.Opacity),
            Xaml = raw.Xaml,
        };
    }

    private string? ResolveLiteral(string key, string? theme, int depth)
    {
        if (depth > 16)
            return null;

        RawValue? raw = null;
        if (theme is not null && _themeValues.TryGetValue(theme, out var values))
            values.TryGetValue(key, out raw);
        raw ??= _globalValues.GetValueOrDefault(key);

        if (raw is null)
            return RuntimeValue(key);
        if (raw.AliasOf is not null)
            return ResolveLiteral(raw.AliasOf, theme, depth + 1);
        if (raw.References is not null)
        {
            var color = ResolveLiteral(raw.References, theme, depth + 1);
            return color is null ? null : WithOpacity(color, raw.Opacity);
        }

        return raw.Literal is null ? null : WithOpacity(raw.Literal, raw.Opacity);
    }

    // Keys that are not defined in XAML but supplied at runtime by ThemeManager or WPF.
    private static string? RuntimeValue(string key)
    {
        if (key.StartsWith("SystemAccentColor", StringComparison.Ordinal))
            return $"runtime: Windows accent color ({key})";
        if (key.StartsWith("SystemColor", StringComparison.Ordinal))
            return $"runtime: Windows high-contrast color ({key})";
        if (StaticMemberRegex().Match(key) is { Success: true } match)
            return $"runtime: {match.Groups[1].Value}";
        return null;
    }

    private static string WithOpacity(string value, string? opacity) =>
        opacity is null ? value : $"{value} (Opacity {opacity})";

    private static List<string> CollectReferences(XElement element)
    {
        var references = new List<string>();
        foreach (var e in element.DescendantsAndSelf())
        {
            foreach (var attribute in e.Attributes())
            {
                if (attribute.IsNamespaceDeclaration)
                    continue;
                if (e.Name.LocalName is "StaticResource" or "DynamicResource" && attribute.Name.LocalName == "ResourceKey")
                    references.Add(NormalizeKey(attribute.Value));
                else
                    references.AddRange(ExtractReferences(attribute.Value));
            }
        }

        return references.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Keys referenced by markup extensions in an attribute value.</summary>
    internal static IEnumerable<string> ExtractReferences(string value)
    {
        foreach (Match match in ResourceReferenceRegex().Matches(value))
            yield return match.Groups["key"].Value;

        foreach (Match match in ThemeKeysReferenceRegex().Matches(value))
            yield return "ThemeKeys." + match.Groups[1].Value;
    }

    private static string? ExtractTypeReference(string? value) =>
        value is not null && TypeReferenceRegex().Match(value) is { Success: true } match
            ? $"{{x:Type {NormalizeTypeName(match.Groups[1].Value)}}}"
            : null;

    internal static string? NormalizeTypeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var name = value.Trim();
        if (TypeReferenceRegex().Match(name) is { Success: true } match)
            name = match.Groups[1].Value;

        var colon = name.LastIndexOf(':');
        return colon >= 0 ? name[(colon + 1)..] : name;
    }

    private static string NormalizeKey(string key)
    {
        var trimmed = key.Trim();
        if (TypeReferenceRegex().Match(trimmed) is { Success: true } typeMatch && trimmed.StartsWith("{x:Type", StringComparison.Ordinal))
            return $"{{x:Type {NormalizeTypeName(typeMatch.Groups[1].Value)}}}";
        return trimmed;
    }

    private static string Snippet(XElement element)
    {
        var xaml = EscapeText(XmlnsDeclarationRegex().Replace(element.ToString(SaveOptions.None), ""));
        return xaml.Length <= MaxXamlLength ? xaml : xaml[..MaxXamlLength] + "\n<!-- truncated -->";
    }

    // Glyph strings are private-use characters; show them as XAML character references.
    private static string EscapeText(string text)
    {
        if (text.All(c => c < 0xE000 || c > 0xF8FF))
            return text;

        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (c is >= '' and <= '')
                sb.Append($"&#x{(int)c:X4};");
            else
                sb.Append(c);
        }

        return sb.ToString();
    }

    private static List<string>? Sorted(List<string>? list) =>
        list is null ? null : list.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    private sealed record RawValue(
        string Type,
        string? Literal = null,
        string? AliasOf = null,
        string? References = null,
        string? Opacity = null,
        string? Xaml = null);

    [GeneratedRegex(@"public\s+static\s+readonly\s+string\s+(\w+)\s*=\s*""([^""]+)""")]
    private static partial Regex ThemeKeyFieldRegex();

    [GeneratedRegex(@"\{\s*(?:\w+:)?(?:DynamicResource|StaticResource|ThemeResource|StaticColor|DynamicColor)\s+(?:ResourceKey\s*=\s*)?(?<key>[A-Za-z_][\w.]*)\s*\}")]
    private static partial Regex ResourceReferenceRegex();

    [GeneratedRegex(@"ThemeKeys\.(\w+)")]
    private static partial Regex ThemeKeysReferenceRegex();

    [GeneratedRegex(@"\{x:Type\s+([\w:.]+)\s*\}")]
    private static partial Regex TypeReferenceRegex();

    [GeneratedRegex(@"^\{x:Static\s+([\w.]+?)(?:Key)?\s*\}$")]
    private static partial Regex StaticMemberRegex();

    [GeneratedRegex(@"\s+xmlns(:\w+)?=""[^""]*""")]
    private static partial Regex XmlnsDeclarationRegex();
}
