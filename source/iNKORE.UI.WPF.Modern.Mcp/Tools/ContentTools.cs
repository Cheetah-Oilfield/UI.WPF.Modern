using System.ComponentModel;
using System.Text;
using iNKORE.UI.WPF.Modern.Mcp.Index;
using ModelContextProtocol.Server;

namespace iNKORE.UI.WPF.Modern.Mcp.Tools;

[McpServerToolType]
public static class ContentTools
{
    private const int MaxSampleCharacters = 40_000;
    private const int MaxElementMatches = 5;
    private const int SnippetLength = 260;

    [McpServerTool(Name = "search_docs", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("Full-text search over the official iNKORE.UI.WPF.Modern documentation (components, theming, overriding resources, window styling, tutorials). Returns the best matching article sections; read one with get_doc.")]
    public static string SearchDocs(
        ServerState state,
        [Description("What to look for, e.g. \"mica backdrop\", \"override button colors\", \"navigation view clear background\".")] string query,
        [Description("Maximum results (default 8).")] int limit = 8)
    {
        var store = state.Store;
        if (store.Index.Docs.Count == 0)
            return "This server was built without documentation.";

        var results = store.DocSearch.Search(query).Take(Math.Clamp(limit, 1, 30)).ToList();
        if (results.Count == 0)
            return $"No documentation matches \"{query}\".";

        var sb = new StringBuilder();
        sb.AppendLine($"{results.Count} sections (best match first). Read one with get_doc(id, section).");
        var rank = 1;
        foreach (var (doc, section, _) in results)
        {
            var location = section.Heading == doc.Title ? doc.Title : $"{doc.Title} › {section.Heading}";
            sb.AppendLine().AppendLine($"{rank++}. {location} — id: {doc.Id}");
            sb.AppendLine($"   {Format.Truncate(section.Markdown, SnippetLength)}");
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "get_doc", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("Returns an official documentation article as markdown, optionally only the sections whose heading matches.")]
    public static string GetDoc(
        ServerState state,
        [Description("Article id from search_docs or get_control (e.g. \"components/navigation/navigation-view\"), or an article title.")] string id,
        [Description("Optional heading filter, e.g. \"Clear background\".")] string? section = null)
    {
        var store = state.Store;
        var doc = store.FindDoc(id);
        if (doc is null)
        {
            var hits = store.DocSearch.Search(id).Select(r => r.Doc.Id).Distinct().Take(5).ToList();
            return $"No article \"{id}\".{(hits.Count > 0 ? " Closest: " + string.Join(", ", hits) : "")}";
        }

        var sections = section is null
            ? doc.Sections
            : doc.Sections.Where(s => s.Heading.Contains(section, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sections.Count == 0)
            return $"\"{doc.Title}\" has no section matching \"{section}\". Sections: {string.Join(", ", doc.Sections.Select(s => s.Heading))}.";

        var sb = new StringBuilder();
        sb.AppendLine($"# {doc.Title}");
        if (doc.Description is not null)
            sb.AppendLine().AppendLine(doc.Description);
        sb.AppendLine().AppendLine($"(id: {doc.Id}; source: iNKORE-NET/Documentation {doc.SourcePath}; links written doc:<id> point to other articles for get_doc)");

        foreach (var s in sections)
        {
            if (s.Level > 1)
                sb.AppendLine().AppendLine($"{new string('#', s.Level)} {s.Heading}");
            sb.AppendLine().AppendLine(s.Markdown);
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "get_samples", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("Working XAML (and C# where available) from the iNKORE.UI.WPF.Modern Gallery app for a control. Falls back to Gallery examples that use the control when it has no page of its own.")]
    public static string GetSamples(
        ServerState state,
        [Description("Control or Gallery page name, e.g. \"NavigationView\", \"InfoBar\", \"Button\".")] string control)
    {
        var store = state.Store;
        var name = control.Trim();
        var colon = name.LastIndexOf(':');
        if (colon >= 0)
            name = name[(colon + 1)..];

        var galleryId = store.FindType(name)?.GalleryId ?? name;
        var samples = store.Index.Samples.Where(s => string.Equals(s.GalleryId, galleryId, StringComparison.OrdinalIgnoreCase)).ToList();
        var fallback = false;
        if (samples.Count == 0)
        {
            samples = store.Index.Samples.Where(s => s.Elements?.Contains(name, StringComparer.OrdinalIgnoreCase) == true).Take(MaxElementMatches).ToList();
            fallback = true;
        }

        if (samples.Count == 0)
            return $"No Gallery samples for \"{control}\". Try search_docs, or get_control to check the name.";

        var prefix = store.Metadata.RecommendedPrefix;
        var sb = new StringBuilder();
        sb.AppendLine(fallback
            ? $"No Gallery page for {name}; Gallery examples that use it:"
            : $"Gallery samples for {galleryId} ({samples.Count}). They assume xmlns:{prefix}=\"{store.Metadata.XmlNamespace}\"; event handlers and x:Name references belong to the sample page.");

        foreach (var sample in samples)
        {
            var block = new StringBuilder();
            block.AppendLine().AppendLine($"## {sample.Title ?? sample.Id} ({sample.Id})");
            if (sample.Xaml is not null)
                block.AppendLine("```xml").AppendLine(sample.Xaml).AppendLine("```");
            if (sample.CSharp is not null)
                block.AppendLine("```csharp").AppendLine(sample.CSharp).AppendLine("```");

            if (sb.Length + block.Length > MaxSampleCharacters)
            {
                sb.AppendLine().AppendLine($"(Output truncated; remaining samples: {string.Join(", ", samples.SkipWhile(s => s != sample).Select(s => s.Id))}.)");
                break;
            }

            sb.Append(block);
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "search_icons", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("Finds real icon names in the library's icon sets: SegoeFluentIcons (Windows Segoe Fluent Icons, e.g. Save, Settings) and FluentSystemIcons (named Name_Size_Variant, e.g. Add_24_Regular). Use it instead of guessing names for {x:Static ui:SegoeFluentIcons.X}.")]
    public static string SearchIcons(
        ServerState state,
        [Description("Words describing the icon, e.g. \"save\", \"arrow left\", \"settings\". FluentSystemIcons names can include size and variant, e.g. \"add 24 filled\".")] string query,
        [Description("Optional icon set: SegoeFluentIcons or FluentSystemIcons.")] string? set = null,
        [Description("Maximum results (default 30).")] int limit = 30)
    {
        var store = state.Store;
        var sets = store.Index.IconSets
            .Where(s => set is null || s.Name.Contains(set, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (sets.Count == 0)
            return $"Unknown icon set \"{set}\". Sets: {string.Join(", ", store.Index.IconSets.Select(s => s.Name))}.";

        var icons = sets.SelectMany(s => s.Icons.Select(i => (Set: s, Name: i.Key, Glyph: i.Value)));
        var results = NameSearch.Rank(icons, i => i.Name, query).Take(Math.Clamp(limit, 1, 200)).ToList();
        if (results.Count == 0)
            return $"No icons match \"{query}\".";

        var prefix = store.Metadata.RecommendedPrefix;
        var first = results[0];
        var sb = new StringBuilder();
        sb.AppendLine($"{results.Count} icons (best match first). Usage: <{prefix}:FontIcon Icon=\"{first.Set.Usage.Replace("{0}", first.Name)}\"/>; the hex code also works as FontIcon Glyph=\"&#x{first.Glyph};\" with the matching FontFamily.");
        sb.AppendLine();
        foreach (var (iconSet, name, glyph) in results)
            sb.AppendLine($"- {iconSet.Name}.{name} ({glyph})");
        return sb.ToString();
    }

    [McpServerTool(Name = "get_server_info", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("Which iNKORE.UI.WPF.Modern version this server describes, where its docs came from, and whether the user's projects reference the same library version.")]
    public static string GetServerInfo(
        ServerState state,
        [Description("Optional folder to check for project references; defaults to the folder the server was started in.")] string? projectPath = null)
    {
        var metadata = state.Store.Metadata;
        var check = projectPath is null
            ? state.StartupCheck
            : ProjectVersionCheck.Check(Path.GetFullPath(projectPath), metadata.LibraryVersion);

        var sb = new StringBuilder();
        sb.AppendLine($"- Library: iNKORE.UI.WPF.Modern {metadata.LibraryVersion}{(metadata.LibraryCommit is null ? "" : $" (commit {metadata.LibraryCommit[..Math.Min(12, metadata.LibraryCommit.Length)]})")}");
        sb.AppendLine(metadata.DocsCommit is null
            ? "- Docs: not included in this build"
            : $"- Docs: iNKORE-NET/Documentation commit {metadata.DocsCommit[..Math.Min(12, metadata.DocsCommit.Length)]} ({metadata.DocsCommitDate:yyyy-MM-dd}). Docs track the latest library and may mention newer APIs.");
        sb.AppendLine($"- Index generated: {metadata.GeneratedAt:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"- Contents: {state.Store.Index.Types.Count} types, {state.Store.Index.Resources.Count} resources, {state.Store.Index.Docs.Count} articles, {state.Store.Index.Samples.Count} samples, {state.Store.Index.IconSets.Sum(s => s.Icons.Count)} icons");
        sb.AppendLine();
        sb.AppendLine($"Project check ({check.Root}): {check.Status switch
        {
            VersionStatus.Match => "projects reference the same version.",
            VersionStatus.Mismatch => "VERSION MISMATCH.",
            VersionStatus.Unknown => "referenced, but the version could not be compared.",
            _ => "no project here references iNKORE.UI.WPF.Modern.",
        }}");
        foreach (var reference in check.References)
            sb.AppendLine($"- {reference.Project}: {reference.Version ?? "(no version)"} via {reference.Source}");

        if (ServerInstructions.VersionWarning(metadata, check) is { } warning)
            sb.AppendLine().AppendLine(warning);
        return sb.ToString();
    }
}
