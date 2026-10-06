using System.ComponentModel;
using System.Text;
using iNKORE.UI.WPF.Modern.Mcp.Index;
using ModelContextProtocol.Server;

namespace iNKORE.UI.WPF.Modern.Mcp.Tools;

[McpServerToolType]
public static class ResourceTools
{
    private const int DefaultLimit = 25;
    private const int MaxListed = 40;

    [McpServerTool(Name = "search_resources", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("Searches the library's theme resources (brushes, colors, thicknesses, corner radii, styles…) by key words. Use it to find the right theme brush for custom UI (e.g. \"card background\", \"text secondary\") or a control's lightweight resources (e.g. \"button background hover\"). Results show per-theme values and the ThemeKeys accessor.")]
    public static string SearchResources(
        ServerState state,
        [Description("Words to match in resource keys, e.g. \"card background\", \"accent text\", \"navigation view pane\". \"hover\" matches PointerOver.")] string query,
        [Description("Optional value type filter: brush, color, style, thickness, double, cornerRadius, or any XAML type name.")] string? kind = null,
        [Description("Maximum results (default 25).")] int limit = DefaultLimit)
    {
        var store = state.Store;
        var candidates = store.Index.Resources.Where(r => MatchesKind(r, kind));
        var results = NameSearch.Rank(candidates, r => r.Key, query).Take(Math.Clamp(limit, 1, 100)).ToList();
        if (results.Count == 0)
            return $"No resources match \"{query}\"{(kind is null ? "" : $" with kind {kind}")}.";

        var sb = new StringBuilder();
        sb.AppendLine($"{results.Count} resources (best match first). Theme-varying values must be referenced with DynamicResource. Use get_resource for usage, aliases and consumers.");
        sb.AppendLine();
        foreach (var r in results)
        {
            var tags = new List<string> { r.ValueType };
            if (r.IsThemeVarying)
                tags.Add("per theme");
            if (r.CodeKey is not null)
                tags.Add(r.CodeKey);
            var values = r.ValueType == "Style" ? (r.TargetType is null ? "" : $"TargetType {r.TargetType}") : Format.Truncate(Format.ThemeValues(r), 160);
            sb.AppendLine($"- {r.Key} ({string.Join(", ", tags)}){(values.Length == 0 ? "" : " — " + values)}");
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "get_resource", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("Details for one theme resource key: its value in each theme (Light, Dark, HighContrast) with alias chains resolved, how to reference or override it in XAML and C#, which controls and styles use it, and its definition.")]
    public static string GetResource(
        ServerState state,
        [Description("Resource key, e.g. \"ButtonBackgroundPointerOver\", \"CardBackgroundFillColorDefaultBrush\", \"AccentButtonStyle\" or \"ThemeKeys.AccentButtonStyleKey\".")] string key)
    {
        var store = state.Store;
        var prefix = store.Metadata.RecommendedPrefix;
        var r = store.FindResource(key);
        if (r is null)
        {
            var suggestions = store.SuggestResources(key).ToList();
            return $"No resource \"{key}\" in iNKORE.UI.WPF.Modern {store.Metadata.LibraryVersion}."
                + (suggestions.Count > 0 ? $" Closest: {string.Join(", ", suggestions)}." : " Try search_resources.");
        }

        var reference = Format.KeyReference(r, prefix);
        var sb = new StringBuilder();
        sb.AppendLine($"# {r.Key}");
        sb.AppendLine();
        sb.AppendLine($"- Type: {r.ValueType}{(r.IsImplicitStyle ? " (implicit style)" : "")}");
        if (r.CodeKey is not null)
            sb.AppendLine($"- C# key: {prefix}:{r.CodeKey} (iNKORE.UI.WPF.Modern.{r.CodeKey})");
        if (r.TargetType is not null)
            sb.AppendLine($"- TargetType: {r.TargetType}");
        if (r.BasedOn is not null)
            sb.AppendLine($"- BasedOn: {r.BasedOn}");
        sb.AppendLine($"- Defined in: {string.Join(", ", r.DefinedIn)}");

        if (r.Themes is { Count: > 0 } themes)
        {
            sb.AppendLine().AppendLine("## Value per theme");
            foreach (var (theme, value) in themes)
            {
                var via = value.AliasOf is not null ? $" (alias of {value.AliasOf})" : value.References is not null ? $" (from {value.References})" : "";
                sb.AppendLine($"- {theme}: {Format.Value(value)}{via}");
                if (value.Xaml is not null && value.Value is null)
                    sb.AppendLine("```xml").AppendLine(value.Xaml).AppendLine("```");
            }
        }
        else if (r.Value is { } value)
        {
            sb.AppendLine().AppendLine("## Value (same in every theme)");
            if (value.Value is not null || value.AliasOf is not null)
                sb.AppendLine($"- {Format.Value(value)}{(value.AliasOf is not null ? $" (alias of {value.AliasOf})" : "")}");
            if (value.Xaml is not null)
                sb.AppendLine("```xml").AppendLine(value.Xaml).AppendLine("```");
        }

        sb.AppendLine().AppendLine("## Usage");
        AppendUsage(sb, r, reference, prefix);

        AppendList(sb, "Used by controls", r.UsedByTypes);
        AppendList(sb, "Referenced by styles/templates", r.UsedByStyles);
        AppendList(sb, "Aliases (keys that forward to this one)", r.AliasedBy);
        AppendList(sb, "References", r.References);
        return sb.ToString();
    }

    private static void AppendUsage(StringBuilder sb, ResourceEntry r, string reference, string prefix)
    {
        if (r.ValueType == "Style")
        {
            if (r.IsImplicitStyle)
            {
                sb.AppendLine($"- Applied automatically to every {r.TargetType} once App.xaml merges {prefix}:ThemeResources and {prefix}:XamlControlsResources.");
                sb.AppendLine($"- Extend it: <Style TargetType=\"{r.TargetType}\" BasedOn=\"{{StaticResource {{x:Type {r.TargetType}}}}}\">");
            }
            else
            {
                sb.AppendLine($"- Apply: Style=\"{{StaticResource {reference}}}\"");
                sb.AppendLine($"- Extend: <Style TargetType=\"{r.TargetType ?? "..."}\" BasedOn=\"{{StaticResource {reference}}}\">");
                if (r.CodeKey is null)
                    sb.AppendLine("- No ThemeKeys accessor: this style is mostly used inside other templates.");
            }

            return;
        }

        if (r.IsThemeVarying)
        {
            sb.AppendLine($"- Reference (value changes with the theme, so DynamicResource is required): {{DynamicResource {reference}}}");
            sb.AppendLine($"- C#: element.SetResourceReference(Border.BackgroundProperty, {(r.CodeKey is not null ? r.CodeKey : $"\"{r.Key}\"")});");
        }
        else
        {
            sb.AppendLine($"- Reference: {{DynamicResource {reference}}} (StaticResource also works; the value does not change per theme)");
        }

        if (r.UsedByTypes is { Count: > 0 })
        {
            var element = r.ValueType.EndsWith("Brush", StringComparison.Ordinal) ? "SolidColorBrush" : r.ValueType;
            var valueAttribute = element == "SolidColorBrush" ? " Color=\"#FF0078D4\"/>" : ">...</" + element + ">";
            sb.AppendLine($"- Override for one element and its children (put in FrameworkElement.Resources) or app-wide (App.xaml, after the merged dictionaries): <{element} x:Key=\"{reference}\"{valueAttribute}");
            sb.AppendLine($"- Forward to another key instead of copying a value: <{prefix}:StaticResource x:Key=\"{reference}\" ResourceKey=\"{{x:Static {prefix}:ThemeKeys.OtherKey}}\"/>");
        }
    }

    private static void AppendList(StringBuilder sb, string title, List<string>? items)
    {
        if (items is not { Count: > 0 })
            return;
        sb.AppendLine().AppendLine($"## {title} ({items.Count})");
        sb.AppendLine(string.Join(", ", items.Take(MaxListed)) + (items.Count > MaxListed ? ", …" : ""));
    }

    private static bool MatchesKind(ResourceEntry r, string? kind) => kind?.ToLowerInvariant() switch
    {
        null or "" or "all" => true,
        "brush" or "brushes" => r.ValueType.EndsWith("Brush", StringComparison.Ordinal),
        "color" or "colors" => r.ValueType == "Color",
        "style" or "styles" => r.ValueType == "Style",
        "thickness" or "margin" or "padding" => r.ValueType == "Thickness",
        "double" or "size" => r.ValueType == "Double",
        "cornerradius" or "radius" => r.ValueType == "CornerRadius",
        _ => string.Equals(r.ValueType, kind, StringComparison.OrdinalIgnoreCase),
    };
}
