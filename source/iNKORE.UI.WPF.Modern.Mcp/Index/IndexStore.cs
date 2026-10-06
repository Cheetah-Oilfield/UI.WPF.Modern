using System.Text.Json;

namespace iNKORE.UI.WPF.Modern.Mcp.Index;

/// <summary>
/// The embedded index plus the lookups the tools need.
/// </summary>
public sealed class IndexStore
{
    public const string ResourceName = "iNKORE.UI.WPF.Modern.Mcp.index.json";

    private readonly Dictionary<string, TypeEntry> _types;
    private readonly Dictionary<string, ResourceEntry> _resources;
    private readonly Dictionary<string, DocArticle> _docs;
    private readonly Dictionary<string, SampleEntry> _samples;

    public IndexStore(LibraryIndex index)
    {
        Index = index;
        _types = index.Types.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        _resources = index.Resources.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);
        _docs = index.Docs.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        _samples = index.Samples.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        DocSearch = new SectionSearch(index.Docs);
    }

    public LibraryIndex Index { get; }

    public IndexMetadata Metadata => Index.Metadata;

    public SectionSearch DocSearch { get; }

    public static IndexStore LoadEmbedded()
    {
        using var stream = typeof(IndexStore).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing; the package was built without an index.");
        var index = JsonSerializer.Deserialize<LibraryIndex>(stream, LibraryIndex.JsonOptions)
            ?? throw new InvalidOperationException("The embedded index is empty.");
        return new IndexStore(index);
    }

    /// <summary>Finds a type by name, accepting "ui:Name", "Name" or a full name.</summary>
    public TypeEntry? FindType(string name)
    {
        var trimmed = name.Trim();
        var colon = trimmed.LastIndexOf(':');
        if (colon >= 0)
            trimmed = trimmed[(colon + 1)..];
        var dot = trimmed.LastIndexOf('.');

        return _types.GetValueOrDefault(trimmed)
            ?? (dot >= 0 ? _types.GetValueOrDefault(trimmed[(dot + 1)..]) : null)
            ?? (trimmed.EndsWith("Extension", StringComparison.OrdinalIgnoreCase) ? null : _types.GetValueOrDefault(trimmed + "Extension"));
    }

    /// <summary>Finds a resource by key, accepting "ThemeKeys.FooKey", "{x:Static ui:ThemeKeys.FooKey}" or "Foo".</summary>
    public ResourceEntry? FindResource(string key)
    {
        var trimmed = key.Trim().Trim('{', '}').Trim();
        if (_resources.TryGetValue(trimmed, out var direct))
            return direct;

        var dot = trimmed.LastIndexOf('.');
        if (dot >= 0)
            trimmed = trimmed[(dot + 1)..];
        if (_resources.TryGetValue(trimmed, out var bare))
            return bare;

        return trimmed.EndsWith("Key", StringComparison.Ordinal) ? _resources.GetValueOrDefault(trimmed[..^3]) : null;
    }

    public DocArticle? FindDoc(string idOrTitle)
    {
        var trimmed = idOrTitle.Trim().Trim('/');
        return _docs.GetValueOrDefault(trimmed)
            ?? Index.Docs.FirstOrDefault(d => string.Equals(d.Title, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? Index.Docs.FirstOrDefault(d => d.Id.EndsWith("/" + trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public SampleEntry? FindSample(string id) => _samples.GetValueOrDefault(id);

    /// <summary>The type and its library base types, most derived first (stops at built-in WPF types).</summary>
    public IEnumerable<TypeEntry> Hierarchy(TypeEntry type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current is not null && seen.Add(current.Name); current = current.BaseType is null ? null : _types.GetValueOrDefault(current.BaseType))
            yield return current;
    }

    /// <summary>Names of types ranked against a query, for "did you mean" suggestions.</summary>
    public IEnumerable<string> SuggestTypes(string query, int count = 5) =>
        NameSearch.Rank(Index.Types, t => t.Name, query).Take(count).Select(t => t.Name);

    public IEnumerable<string> SuggestResources(string query, int count = 5) =>
        NameSearch.Rank(Index.Resources, r => r.Key, query).Take(count).Select(r => r.Key);
}
