using iNKORE.UI.WPF.Modern.Mcp.Index;

namespace iNKORE.UI.WPF.Modern.Mcp.Tools;

internal static class Format
{
    /// <summary>Text safe for a markdown table cell.</summary>
    public static string Cell(string? text) =>
        string.IsNullOrEmpty(text) ? "" : text.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");

    public static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var flat = text.Replace("\r", "").Replace('\n', ' ');
        return flat.Length <= max ? flat : flat[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>How the type is written in XAML: ui:NavigationView, Button, ui:StaticResource (markup extension).</summary>
    public static string XamlName(TypeEntry type, string prefix)
    {
        var name = type.Kind == TypeKind.MarkupExtension && type.Name.EndsWith("Extension", StringComparison.Ordinal)
            ? type.Name[..^"Extension".Length]
            : type.Name;
        return IsWpf(type) || !type.InXmlNamespace ? name : $"{prefix}:{name}";
    }

    public static bool IsWpf(TypeEntry type) => type.Namespace.StartsWith("System.", StringComparison.Ordinal);

    /// <summary>One-line description: XML doc summary, else the Gallery subtitle.</summary>
    public static string? Summary(TypeEntry type) => type.Summary ?? type.GallerySummary;

    /// <summary>The XAML to reference a resource key, preferring the ThemeKeys accessor.</summary>
    public static string KeyReference(ResourceEntry resource, string prefix) =>
        resource.CodeKey is not null ? $"{{x:Static {prefix}:{resource.CodeKey}}}" : resource.Key;

    public static string ThemeValues(ResourceEntry resource)
    {
        if (resource.Themes is { Count: > 0 } themes)
            return string.Join(" · ", themes.Select(t => $"{t.Key}: {Value(t.Value)}"));
        return resource.Value is null ? "" : Value(resource.Value);
    }

    public static string Value(ThemeValue value) =>
        value.Value ?? (value.Xaml is not null ? "(XAML)" : value.AliasOf ?? value.References ?? "");
}
