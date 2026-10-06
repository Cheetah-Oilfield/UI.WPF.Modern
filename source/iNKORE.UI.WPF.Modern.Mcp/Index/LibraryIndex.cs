using System.Text.Json;
using System.Text.Json.Serialization;

namespace iNKORE.UI.WPF.Modern.Mcp.Index;

// The index model is shared between IndexGen (writer) and the MCP server (reader).
// IndexGen links this file in, so both always agree on the shape of index.json.

public sealed class LibraryIndex
{
    public required IndexMetadata Metadata { get; init; }
    public List<TypeEntry> Types { get; init; } = [];
    public List<ResourceEntry> Resources { get; init; } = [];
    public List<DocArticle> Docs { get; init; } = [];
    public List<SampleEntry> Samples { get; init; } = [];
    public List<IconSet> IconSets { get; init; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

public sealed class IndexMetadata
{
    public required string LibraryVersion { get; init; }
    public string? LibraryCommit { get; init; }
    public string? DocsCommit { get; init; }
    public DateTimeOffset? DocsCommitDate { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>The XAML namespace URI that maps the library's CLR namespaces.</summary>
    public required string XmlNamespace { get; init; }

    /// <summary>The prefix the docs and Gallery use for <see cref="XmlNamespace"/>.</summary>
    public required string RecommendedPrefix { get; init; }

    public List<string> XmlNamespaceClrNamespaces { get; init; } = [];
}

public enum TypeKind
{
    Control,
    Element,
    Object,
    Helper,
    MarkupExtension,
    Converter,
    ResourceDictionary,
    Enum,
}

public sealed class TypeEntry
{
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public required string Namespace { get; init; }
    public required string Assembly { get; init; }

    // Control is the enum default, so it must not be skipped by WhenWritingDefault.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public TypeKind Kind { get; init; }
    public string? BaseType { get; init; }
    public bool IsAbstract { get; init; }
    public bool IsObsolete { get; init; }
    public string? ObsoleteMessage { get; init; }
    public string? Summary { get; set; }

    /// <summary>True when the type's namespace is mapped to the library's XAML namespace.</summary>
    public bool InXmlNamespace { get; init; }

    public string? ContentProperty { get; init; }

    /// <summary>Gallery category (e.g. "Menus and Toolbars"), when the Gallery has a page for the type.</summary>
    public string? Category { get; set; }
    public string? GalleryId { get; set; }
    public string? GallerySummary { get; set; }

    public List<PropertyEntry>? Properties { get; set; }
    public List<EventEntry>? Events { get; set; }
    public List<TemplatePartEntry>? TemplateParts { get; set; }
    public List<VisualStateEntry>? VisualStates { get; set; }
    public List<EnumValueEntry>? EnumValues { get; set; }

    /// <summary>Key of the style applied by default (implicit style or Default{Name}Style).</summary>
    public string? DefaultStyle { get; set; }

    /// <summary>All named styles whose TargetType is this type, including <see cref="DefaultStyle"/>.</summary>
    public List<string>? Styles { get; set; }

    public List<string>? DocIds { get; set; }
    public List<string>? SampleIds { get; set; }
}

public sealed class PropertyEntry
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string? Summary { get; set; }
    public bool IsDependencyProperty { get; init; }
    public bool IsAttached { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsObsolete { get; init; }

    /// <summary>For attached properties, the type the getter accepts (e.g. DependencyObject, Window).</summary>
    public string? AttachedTo { get; init; }
}

public sealed class EventEntry
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string? Summary { get; set; }
    public bool IsRouted { get; init; }
}

public sealed class TemplatePartEntry
{
    public required string Name { get; init; }
    public string? Type { get; init; }
}

public sealed class VisualStateEntry
{
    public required string Name { get; init; }
    public string? GroupName { get; init; }
}

public sealed class EnumValueEntry
{
    public required string Name { get; init; }
    public long Value { get; init; }
    public string? Summary { get; set; }
}

public sealed class ResourceEntry
{
    public required string Key { get; init; }

    /// <summary>XAML element type of the resource, e.g. SolidColorBrush, Color, Thickness, Style.</summary>
    public required string ValueType { get; set; }

    /// <summary>True when the value differs per theme (defined in the Light/Dark/HighContrast schemes).</summary>
    public bool IsThemeVarying { get; set; }

    /// <summary>Value per theme (Light, Dark, HighContrast) for theme-varying resources.</summary>
    public Dictionary<string, ThemeValue>? Themes { get; set; }

    /// <summary>Value for theme-independent resources.</summary>
    public ThemeValue? Value { get; set; }

    /// <summary>Library-relative XAML files that define this key.</summary>
    public List<string> DefinedIn { get; init; } = [];

    /// <summary>C# accessor for the key, e.g. ThemeKeys.AccentButtonStyleKey.</summary>
    public string? CodeKey { get; set; }

    // Styles only.
    public string? TargetType { get; set; }
    public string? BasedOn { get; set; }
    public bool IsImplicitStyle { get; set; }

    /// <summary>For styles and templates: resource keys referenced inside.</summary>
    public List<string>? References { get; set; }

    /// <summary>Styles that reference this resource.</summary>
    public List<string>? UsedByStyles { get; set; }

    /// <summary>Target types of the styles that reference this resource.</summary>
    public List<string>? UsedByTypes { get; set; }

    /// <summary>Resources that are aliases of this one (m:StaticResource ResourceKey=...).</summary>
    public List<string>? AliasedBy { get; set; }
}

public sealed class ThemeValue
{
    /// <summary>Resolved, human-readable value (e.g. "#FF0067C0", "11,5,11,5", "{accent: SystemAccentColorDark1}").</summary>
    public string? Value { get; init; }

    /// <summary>Key this resource is an alias of, when defined via m:StaticResource.</summary>
    public string? AliasOf { get; init; }

    /// <summary>Key referenced by the value (e.g. the Color of a SolidColorBrush), when not a literal.</summary>
    public string? References { get; init; }

    /// <summary>Raw XAML for complex values (gradients, styles), trimmed.</summary>
    public string? Xaml { get; init; }
}

public sealed class DocArticle
{
    /// <summary>Slug path, e.g. "components/basic-input/button".</summary>
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public required string SourcePath { get; init; }
    public List<DocSection> Sections { get; init; } = [];
}

public sealed class DocSection
{
    public required string Heading { get; init; }
    public int Level { get; init; }
    public required string Markdown { get; init; }
}

public sealed class IconSet
{
    /// <summary>Static class holding the icons, e.g. SegoeFluentIcons.</summary>
    public required string Name { get; init; }

    /// <summary>XAML usage with a {0} placeholder for the icon name.</summary>
    public required string Usage { get; init; }

    /// <summary>Icon name -> glyph code point in hex (e.g. "Save" -> "E74E").</summary>
    public Dictionary<string, string> Icons { get; init; } = [];
}

public sealed class SampleEntry
{
    /// <summary>"{GalleryId}/{ExampleName}", e.g. "Button/Example1".</summary>
    public required string Id { get; init; }
    public required string GalleryId { get; init; }
    public string? Title { get; init; }
    public string? Xaml { get; init; }
    public string? CSharp { get; init; }

    /// <summary>Element names used in the XAML, without prefixes.</summary>
    public List<string>? Elements { get; init; }
}
