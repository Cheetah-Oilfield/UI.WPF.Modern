using System.Diagnostics;
using System.Text.Json;
using iNKORE.UI.WPF.Modern.Mcp.Index;

namespace iNKORE.UI.WPF.Modern.Mcp.IndexGen;

/// <summary>
/// Builds the index.json embedded in iNKORE.UI.WPF.Modern.Mcp from the built library assemblies,
/// XML docs, theme XAML, Gallery samples and a Documentation repo checkout.
/// </summary>
internal static class Program
{
    // The prefix the docs and Gallery use for the library's XAML namespace.
    private const string RecommendedPrefix = "ui";

    private const string Usage = """
        Usage: IndexGen --out <index.json> [options]

          --out <path>             Output file (required).
          --repo <path>            UI.WPF.Modern repo root (default: found by walking up from the current directory).
          --docs <path>            iNKORE-NET/Documentation checkout. Without it, docs are skipped.
          --require-docs           Fail instead of skipping when --docs is missing or invalid.
          --configuration <name>   Library build configuration (default: Release).
          --framework <tfm>        Library target framework to read (default: net6.0-windows10.0.18362.0).
          --indented               Write indented JSON.
        """;

    private static int Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            if (options is null)
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }

            Run(options);
            return 0;
        }
        catch (Exception ex) when (ex is IndexGenException or DirectoryNotFoundException or FileNotFoundException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static void Run(Options options)
    {
        var stopwatch = Stopwatch.StartNew();
        var repo = options.Repo;
        var coreProject = Path.Combine(repo, "source", "iNKORE.UI.WPF.Modern");
        var controlsProject = Path.Combine(repo, "source", "iNKORE.UI.WPF.Modern.Controls");
        var galleryProject = Path.Combine(repo, "source", "iNKORE.UI.WPF.Modern.Gallery");

        var coreBin = Path.Combine(coreProject, "bin", options.Configuration, options.Framework);
        var controlsBin = Path.Combine(controlsProject, "bin", options.Configuration, options.Framework);
        var coreDll = Path.Combine(coreBin, "iNKORE.UI.WPF.Modern.dll");
        var controlsDll = Path.Combine(controlsBin, "iNKORE.UI.WPF.Modern.Controls.dll");
        foreach (var dll in new[] { coreDll, controlsDll })
        {
            if (!File.Exists(dll))
                throw new IndexGenException($"{dll} not found. Build the library first: dotnet build source/iNKORE.UI.WPF.Modern.Controls -c {options.Configuration}");
        }

        // API surface.
        var xmlDocs = new XmlDocReader();
        xmlDocs.Load(Path.ChangeExtension(coreDll, ".xml"));
        xmlDocs.Load(Path.ChangeExtension(controlsDll, ".xml"));

        var resolverPaths = AssemblyScanner.BuildResolverPaths(
            [coreBin, controlsBin],
            Path.Combine(controlsProject, "obj", "project.assets.json"),
            options.Framework);

        using var assemblies = new AssemblyScanner(resolverPaths, xmlDocs);
        var core = assemblies.Load(coreDll);
        var controls = assemblies.Load(controlsDll);
        var (version, xmlNamespace, coreNamespaces) = assemblies.ReadAssemblyInfo(core);
        var (_, _, controlsNamespaces) = assemblies.ReadAssemblyInfo(controls);
        var clrNamespaces = coreNamespaces.Concat(controlsNamespaces).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var clrNamespaceSet = clrNamespaces.ToHashSet(StringComparer.Ordinal);

        var types = assemblies.Scan(core, clrNamespaceSet).Concat(assemblies.Scan(controls, clrNamespaceSet)).ToList();
        Console.Error.WriteLine($"types: {types.Count}");

        // Theme resources and styles.
        var resourceScanner = new XamlResourceScanner(repo);
        resourceScanner.ReadThemeKeys(Directory.GetFiles(coreProject, "ThemeKeys*.cs"));
        foreach (var theme in XamlResourceScanner.ThemeNames)
            resourceScanner.ScanThemeScheme(theme, Path.Combine(coreProject, "Themes", "Schemes", theme + ".xaml"));
        foreach (var file in LibraryDictionaries(coreProject).Concat(LibraryDictionaries(controlsProject)))
            resourceScanner.ScanDictionary(file);

        var resources = resourceScanner.Build();
        Console.Error.WriteLine($"resources: {resources.Count}");
        LinkStyles(types, resources, assemblies);

        // Gallery samples.
        var gallery = new GallerySampleScanner();
        var catalog = gallery.ReadCatalog(galleryProject);
        var samples = gallery.ReadSamples(galleryProject, catalog);
        Console.Error.WriteLine($"gallery items: {catalog.Count}, samples: {samples.Count}");
        LinkGallery(types, catalog, samples);

        // Icon names, so assistants use real x:Static icon keys.
        var iconSets = IconScanner.Scan(Path.Combine(coreProject, "Common", "IconKeys"), RecommendedPrefix);
        Console.Error.WriteLine($"icons: {string.Join(", ", iconSets.Select(s => $"{s.Name}={s.Icons.Count}"))}");

        // Documentation.
        var docs = new List<DocArticle>();
        (string? Commit, DateTimeOffset? Date) docsCommit = (null, null);
        if (options.Docs is not null && Directory.Exists(Path.Combine(options.Docs, DocsScanner.DocsRelativePath)))
        {
            docs = DocsScanner.Scan(options.Docs);
            docsCommit = GitCommit(options.Docs);
            LinkDocs(types, docs);
            Console.Error.WriteLine($"docs: {docs.Count}");
        }
        else if (options.RequireDocs)
        {
            throw new IndexGenException($"Documentation checkout not found{(options.Docs is null ? " (pass --docs)" : $" at {options.Docs}")}.");
        }
        else
        {
            Console.Error.WriteLine("warning: no Documentation checkout; the index will not contain docs. Pass --docs <path>.");
        }

        var index = new LibraryIndex
        {
            Metadata = new IndexMetadata
            {
                LibraryVersion = version,
                LibraryCommit = GitCommit(repo).Commit,
                DocsCommit = docsCommit.Commit,
                DocsCommitDate = docsCommit.Date,
                GeneratedAt = DateTimeOffset.UtcNow,
                XmlNamespace = xmlNamespace,
                RecommendedPrefix = RecommendedPrefix,
                XmlNamespaceClrNamespaces = clrNamespaces,
            },
            Types = types.OrderBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.FullName, StringComparer.Ordinal).ToList(),
            Resources = resources,
            Docs = docs,
            Samples = samples,
            IconSets = iconSets,
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Out))!);
        var json = JsonSerializer.Serialize(index, new JsonSerializerOptions(LibraryIndex.JsonOptions) { WriteIndented = options.Indented });
        File.WriteAllText(options.Out, json);

        Console.Error.WriteLine($"wrote {options.Out} ({json.Length / 1024:N0} KB, version {version}) in {stopwatch.Elapsed.TotalSeconds:N1}s");
    }

    private static IEnumerable<string> LibraryDictionaries(string projectDirectory) =>
        Directory.GetFiles(projectDirectory, "*.xaml", SearchOption.AllDirectories)
            .Where(path =>
            {
                var relative = Path.GetRelativePath(projectDirectory, path).Replace('\\', '/');
                return !relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
                    && !relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
                    && !relative.StartsWith("Properties/", StringComparison.OrdinalIgnoreCase)
                    && !relative.Contains("/DesignTime/", StringComparison.OrdinalIgnoreCase)
                    && !relative.StartsWith("Themes/Schemes/", StringComparison.OrdinalIgnoreCase);
            })
            .Order(StringComparer.Ordinal);

    private static void LinkStyles(List<TypeEntry> types, List<ResourceEntry> resources, AssemblyScanner assemblies)
    {
        var stylesByTarget = resources
            .Where(r => r.ValueType == "Style" && r.TargetType is not null)
            .GroupBy(r => r.TargetType!, StringComparer.Ordinal);

        foreach (var group in stylesByTarget)
        {
            var type = types.FirstOrDefault(t => t.Name == group.Key);
            if (type is null)
            {
                // A built-in WPF type the library restyles, e.g. Button or TextBlock.
                type = assemblies.TryCreateWpfEntry(group.Key);
                if (type is null)
                    continue;
                types.Add(type);
            }

            var keys = group.Select(r => r.Key).Order(StringComparer.Ordinal).ToList();
            type.Styles = keys;
            type.DefaultStyle = keys.FirstOrDefault(k => k == $"{{x:Type {type.Name}}}")
                ?? keys.FirstOrDefault(k => k == $"Default{type.Name}Style");
        }
    }

    private static void LinkGallery(List<TypeEntry> types, List<GalleryItem> catalog, List<SampleEntry> samples)
    {
        var samplesByPage = samples.GroupBy(s => s.GalleryId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(s => s.Id).ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var item in catalog)
        {
            var type = types.FirstOrDefault(t => string.Equals(t.Name, item.UniqueId, StringComparison.OrdinalIgnoreCase) && t.Kind != TypeKind.Enum);
            if (type is null)
                continue;

            type.Category ??= item.Category;
            type.GalleryId ??= item.UniqueId;
            type.GallerySummary ??= item.Summary;
            if (samplesByPage.TryGetValue(item.UniqueId, out var ids))
                type.SampleIds = ids;
        }
    }

    private static void LinkDocs(List<TypeEntry> types, List<DocArticle> docs)
    {
        static string Normalize(string s) => s.Replace("-", "").Replace("_", "").ToLowerInvariant();

        foreach (var doc in docs)
        {
            var slug = Normalize(doc.Id.Split('/')[^1]);
            foreach (var type in types.Where(t => Normalize(t.Name) == slug || (t.GalleryId is not null && Normalize(t.GalleryId) == slug)))
                (type.DocIds ??= []).Add(doc.Id);
        }
    }

    private static (string? Commit, DateTimeOffset? Date) GitCommit(string directory)
    {
        var output = RunGit(directory, "log -1 --format=%H%n%cI");
        if (output is null)
            return (null, null);

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (lines.ElementAtOrDefault(0), DateTimeOffset.TryParse(lines.ElementAtOrDefault(1), out var date) ? date : null);
    }

    private static string? RunGit(string directory, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", $"-C \"{directory}\" {arguments}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private sealed class Options
    {
        public required string Out { get; init; }
        public required string Repo { get; init; }
        public string? Docs { get; init; }
        public bool RequireDocs { get; init; }
        public string Configuration { get; init; } = "Release";
        public string Framework { get; init; } = "net6.0-windows10.0.18362.0";
        public bool Indented { get; init; }

        public static Options? Parse(string[] args)
        {
            string? output = null, repo = null, docs = null, configuration = null, framework = null;
            bool requireDocs = false, indented = false;

            for (var i = 0; i < args.Length; i++)
            {
                string? Next() => i + 1 < args.Length ? args[++i] : null;

                switch (args[i])
                {
                    case "--out": output = Next(); break;
                    case "--repo": repo = Next(); break;
                    case "--docs": docs = Next(); break;
                    case "--configuration": configuration = Next(); break;
                    case "--framework": framework = Next(); break;
                    case "--require-docs": requireDocs = true; break;
                    case "--indented": indented = true; break;
                    default:
                        Console.Error.WriteLine($"Unknown argument: {args[i]}");
                        return null;
                }
            }

            if (output is null)
                return null;

            return new Options
            {
                Out = output,
                Repo = Path.GetFullPath(repo ?? FindRepoRoot() ?? throw new IndexGenException("Could not find the repo root; pass --repo.")),
                Docs = docs is null ? null : Path.GetFullPath(docs),
                RequireDocs = requireDocs,
                Configuration = configuration ?? "Release",
                Framework = framework ?? "net6.0-windows10.0.18362.0",
                Indented = indented,
            };
        }

        private static string? FindRepoRoot()
        {
            for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "iNKORE.UI.WPF.Modern.sln")))
                    return dir.FullName;
            }

            return null;
        }
    }
}

internal sealed class IndexGenException(string message) : Exception(message);
