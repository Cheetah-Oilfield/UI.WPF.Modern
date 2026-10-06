using System.ComponentModel;
using System.Text;
using iNKORE.UI.WPF.Modern.Mcp.Index;
using ModelContextProtocol.Server;

namespace iNKORE.UI.WPF.Modern.Mcp.Tools;

[McpServerToolType]
public static class ControlTools
{
    private const int MaxLightweightResources = 80;
    private const int MaxInternalStyles = 10;

    private static readonly Dictionary<string, TypeKind[]> KindFilters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["controls"] = [TypeKind.Control, TypeKind.Element],
        ["control"] = [TypeKind.Control],
        ["element"] = [TypeKind.Element],
        ["helper"] = [TypeKind.Helper],
        ["markupExtension"] = [TypeKind.MarkupExtension],
        ["converter"] = [TypeKind.Converter],
        ["resourceDictionary"] = [TypeKind.ResourceDictionary],
        ["enum"] = [TypeKind.Enum],
        ["object"] = [TypeKind.Object],
        ["all"] = Enum.GetValues<TypeKind>(),
    };

    [McpServerTool(Name = "list_controls", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("Lists what iNKORE.UI.WPF.Modern provides, grouped by Gallery category: its controls, plus the built-in WPF controls it restyles. Use it to check whether a control exists before writing XAML.")]
    public static string ListControls(
        ServerState state,
        [Description("Optional category filter, e.g. \"Navigation\", \"Basic Input\", \"Dialogs and Flyouts\".")] string? category = null,
        [Description("What to list: controls (default; controls and elements), helper (attached-property classes such as ControlHelper, WindowHelper), markupExtension, converter, resourceDictionary, enum, object (non-visual types such as flyouts), or all.")] string? kind = null)
    {
        var prefix = state.Store.Metadata.RecommendedPrefix;
        if (!KindFilters.TryGetValue(kind ?? "controls", out var kinds))
            return $"Unknown kind \"{kind}\". Use one of: {string.Join(", ", KindFilters.Keys)}.";

        var types = state.Store.Index.Types
            .Where(t => kinds.Contains(t.Kind))
            .Where(t => !Format.IsWpf(t) || t.Category is not null || kinds.Length > 2)
            .Where(t => category is null || (t.Category ?? "").Contains(category, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (types.Count == 0)
            return category is null ? "Nothing matches." : $"No types in category \"{category}\". Categories: {string.Join(", ", Categories(state))}.";

        var sb = new StringBuilder();
        sb.AppendLine($"iNKORE.UI.WPF.Modern {state.Store.Metadata.LibraryVersion}: {types.Count} types. xmlns:{prefix}=\"{state.Store.Metadata.XmlNamespace}\". Built-in WPF controls marked (WPF) are restyled by the library and need no prefix.");
        foreach (var group in types.GroupBy(t => t.Category ?? "Other").OrderBy(g => g.Key == "Other" ? 1 : 0).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine().AppendLine($"## {group.Key}");
            foreach (var type in group.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                var tags = new List<string>();
                if (Format.IsWpf(type))
                    tags.Add("WPF");
                if (kinds.Length > 1 && !Format.IsWpf(type))
                    tags.Add(KindName(type.Kind));
                if (type.IsObsolete)
                    tags.Add("obsolete");
                var tagText = tags.Count == 0 ? "" : $" ({string.Join(", ", tags)})";
                var summary = Format.Summary(type);
                sb.AppendLine($"- {Format.XamlName(type, prefix)}{tagText}{(summary is null ? "" : " — " + Format.Truncate(summary, 140))}");
            }
        }

        sb.AppendLine().AppendLine("Use get_control for properties, styles and lightweight resources.");
        return sb.ToString();
    }

    [McpServerTool(Name = "get_control", ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("Full reference for one type: XAML usage and namespace, properties (including attached and dependency properties) with descriptions, events, enum values, template parts, named styles with how to apply them, the lightweight theme resources that restyle it, related doc articles and Gallery samples. Works for library types (NavigationView, ContentDialog, ControlHelper, ThemeManager…) and restyled WPF controls (Button, TextBox…).")]
    public static string GetControl(
        ServerState state,
        [Description("Type name, e.g. \"NavigationView\", \"ui:InfoBar\", \"WindowHelper\", \"Button\".")] string name)
    {
        var store = state.Store;
        var prefix = store.Metadata.RecommendedPrefix;
        var type = store.FindType(name);
        if (type is null)
        {
            // The Gallery names some pages after WinUI controls that WPF provides differently (e.g. CalendarView shows Calendar).
            var galleryHint = "";
            var pageSamples = store.Index.Samples.Where(s => string.Equals(s.GalleryId, name.Split(':')[^1], StringComparison.OrdinalIgnoreCase)).ToList();
            if (pageSamples.Count > 0)
            {
                var elements = pageSamples.SelectMany(s => s.Elements ?? []).Where(e => store.FindType(e) is not null).Distinct().Take(8);
                galleryHint = $" The Gallery has a \"{pageSamples[0].GalleryId}\" page whose samples use: {string.Join(", ", elements)} (see get_samples).";
            }

            var suggestions = store.SuggestTypes(name).ToList();
            return $"\"{name}\" is not a type in iNKORE.UI.WPF.Modern {store.Metadata.LibraryVersion}." + galleryHint
                + (suggestions.Count > 0 ? $" Similar names: {string.Join(", ", suggestions)}." : "")
                + " It may be a WinUI/UWP API that this WPF port does not have; use list_controls to see what exists.";
        }

        var sb = new StringBuilder();
        var xamlName = Format.XamlName(type, prefix);
        sb.AppendLine($"# {type.Name}");
        sb.AppendLine();
        sb.AppendLine($"- Kind: {KindName(type.Kind)}{(type.IsAbstract ? " (abstract)" : "")}");
        sb.AppendLine($"- Namespace: {type.Namespace} ({type.Assembly})");
        if (!Format.IsWpf(type))
        {
            sb.AppendLine(type.InXmlNamespace
                ? $"- XAML: {xamlName} with xmlns:{prefix}=\"{store.Metadata.XmlNamespace}\""
                : $"- XAML: not in the {prefix}: namespace; use xmlns:x=\"clr-namespace:{type.Namespace};assembly={type.Assembly}\"");
        }
        else
        {
            sb.AppendLine("- Built-in WPF type, restyled by the library's theme. No prefix needed.");
        }

        // Library base types, then the first base outside the index (usually a WPF type).
        var chain = store.Hierarchy(type).ToList();
        var bases = chain.Skip(1).Select(t => t.Name).ToList();
        if (chain[^1].BaseType is { } external)
            bases.Add(external);
        if (bases.Count > 0)
            sb.AppendLine($"- Base: {string.Join(" → ", bases)}");
        if (type.Category is not null)
            sb.AppendLine($"- Gallery category: {type.Category}");
        if (type.ContentProperty is not null)
            sb.AppendLine($"- Content property: {type.ContentProperty}");
        if (type.IsObsolete)
            sb.AppendLine($"- OBSOLETE{(type.ObsoleteMessage is null ? "" : ": " + type.ObsoleteMessage)}");

        if (Format.Summary(type) is { } summary)
            sb.AppendLine().AppendLine(summary);

        if (type.Kind == TypeKind.Enum)
        {
            AppendEnumValues(sb, type);
            AppendLinks(sb, type, store);
            return sb.ToString();
        }

        AppendMembers(sb, type, store, prefix);
        AppendStyles(sb, type, store, prefix);
        AppendLightweightResources(sb, type, store);
        AppendLinks(sb, type, store);
        return sb.ToString();
    }

    private static void AppendMembers(StringBuilder sb, TypeEntry type, IndexStore store, string prefix)
    {
        var enums = new List<TypeEntry>();
        foreach (var owner in store.Hierarchy(type))
        {
            var inherited = owner != type;
            var properties = owner.Properties ?? [];
            var attached = properties.Where(p => p.IsAttached).ToList();
            var regular = properties.Where(p => !p.IsAttached).ToList();

            if (regular.Count > 0)
            {
                sb.AppendLine().AppendLine(inherited ? $"## Properties inherited from {owner.Name}" : "## Properties");
                sb.AppendLine("| Name | Type | Notes | Description |");
                sb.AppendLine("| --- | --- | --- | --- |");
                foreach (var property in regular)
                {
                    var notes = new List<string>();
                    if (property.IsDependencyProperty)
                        notes.Add("DP");
                    if (property.IsReadOnly)
                        notes.Add("read-only");
                    if (property.IsObsolete)
                        notes.Add("obsolete");
                    sb.AppendLine($"| {property.Name} | {Format.Cell(property.Type)} | {string.Join(", ", notes)} | {Format.Cell(property.Summary)} |");
                    CollectEnum(property.Type, store, enums);
                }
            }

            if (attached.Count > 0)
            {
                var ownerName = Format.XamlName(owner, prefix);
                sb.AppendLine().AppendLine(inherited ? $"## Attached properties inherited from {owner.Name}" : "## Attached properties");
                sb.AppendLine("| XAML attribute | Type | Applies to | Description |");
                sb.AppendLine("| --- | --- | --- | --- |");
                foreach (var property in attached)
                {
                    var readOnly = property.IsReadOnly ? " (read-only)" : "";
                    sb.AppendLine($"| {ownerName}.{property.Name}{readOnly} | {Format.Cell(property.Type)} | {Format.Cell(property.AttachedTo)} | {Format.Cell(property.Summary)} |");
                    CollectEnum(property.Type, store, enums);
                }
            }

            if (owner.Events is { Count: > 0 } events)
            {
                sb.AppendLine().AppendLine(inherited ? $"## Events inherited from {owner.Name}" : "## Events");
                foreach (var e in events)
                    sb.AppendLine($"- {e.Name} ({e.Type}{(e.IsRouted ? ", routed" : "")}){(e.Summary is null ? "" : " — " + e.Summary)}");
            }

            if (!inherited && owner.TemplateParts is { Count: > 0 } parts)
                sb.AppendLine().AppendLine("Template parts: " + string.Join(", ", parts.Select(p => p.Type is null ? p.Name : $"{p.Name} ({p.Type})")));
            if (!inherited && owner.VisualStates is { Count: > 0 } states)
                sb.AppendLine().AppendLine("Visual states: " + string.Join(", ", states.Select(s => s.GroupName is null ? s.Name : $"{s.GroupName}.{s.Name}")));
        }

        if (enums.Count > 0)
        {
            sb.AppendLine().AppendLine("## Enum values used above");
            foreach (var e in enums)
                sb.AppendLine($"- {e.Name}: {string.Join(", ", (e.EnumValues ?? []).Select(v => v.Name))}");
        }
    }

    private static void CollectEnum(string typeName, IndexStore store, List<TypeEntry> enums)
    {
        if (store.FindType(typeName.TrimEnd('?')) is { Kind: TypeKind.Enum } e && !enums.Contains(e))
            enums.Add(e);
    }

    private static void AppendEnumValues(StringBuilder sb, TypeEntry type)
    {
        sb.AppendLine().AppendLine("## Values");
        foreach (var value in type.EnumValues ?? [])
            sb.AppendLine($"- {value.Name} = {value.Value}{(value.Summary is null ? "" : " — " + value.Summary)}");
    }

    private static void AppendStyles(StringBuilder sb, TypeEntry type, IndexStore store, string prefix)
    {
        if (type.Styles is not { Count: > 0 } styleKeys)
            return;

        var styles = styleKeys.Select(store.FindResource).OfType<ResourceEntry>().ToList();
        var named = styles.Where(s => s.CodeKey is not null).ToList();
        var internalStyles = styles.Where(s => s.CodeKey is null && !s.IsImplicitStyle).ToList();

        sb.AppendLine().AppendLine("## Styles");
        if (type.DefaultStyle is not null)
            sb.AppendLine($"- Default style ({type.DefaultStyle}) is applied automatically once App.xaml merges {prefix}:ThemeResources and {prefix}:XamlControlsResources.");
        foreach (var style in named)
            sb.AppendLine($"- {style.Key}{(style.BasedOn is null ? "" : $" (BasedOn {style.BasedOn})")}: Style=\"{{StaticResource {{x:Static {prefix}:{style.CodeKey}}}}}\"");
        if (internalStyles.Count > 0)
            sb.AppendLine($"- Used internally by other templates (no ThemeKeys accessor): {string.Join(", ", internalStyles.Take(MaxInternalStyles).Select(s => s.Key))}{(internalStyles.Count > MaxInternalStyles ? ", …" : "")}");
    }

    private static void AppendLightweightResources(StringBuilder sb, TypeEntry type, IndexStore store)
    {
        // Only what the user-facing styles read: the default style, public named styles and their BasedOn chains.
        // Styles used inside other controls' templates would drag in unrelated keys.
        var styles = (type.Styles ?? [])
            .Select(store.FindResource)
            .OfType<ResourceEntry>()
            .Where(s => s.Key == type.DefaultStyle || s.CodeKey is not null);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var style in styles)
        {
            for (var current = style; current is not null && visited.Add(current.Key); current = current.BasedOn is null ? null : store.FindResource(current.BasedOn))
                keys.UnionWith(current.References ?? []);
        }

        var resources = keys
            .Select(store.FindResource)
            .OfType<ResourceEntry>()
            .Where(r => r.ValueType is not ("Style" or "ControlTemplate" or "DataTemplate"))
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .ToList();
        if (resources.Count == 0)
            return;

        sb.AppendLine().AppendLine($"## Lightweight resources ({resources.Count})");
        sb.AppendLine("Keys this type's styles read via DynamicResource. Override them in FrameworkElement.Resources or App.xaml to restyle it, including hover/pressed/disabled states. Details and per-theme values: get_resource.");
        foreach (var group in resources.GroupBy(r => r.ValueType).OrderByDescending(g => g.Count()))
        {
            var groupKeys = group.Select(r => r.Key).Take(MaxLightweightResources).ToList();
            sb.AppendLine($"- {group.Key}: {string.Join(", ", groupKeys)}{(group.Count() > groupKeys.Count ? ", …" : "")}");
        }
    }

    private static void AppendLinks(StringBuilder sb, TypeEntry type, IndexStore store)
    {
        if (type.DocIds is { Count: > 0 } docIds)
        {
            sb.AppendLine().AppendLine("## Docs (get_doc)");
            foreach (var id in docIds)
                sb.AppendLine($"- {id}{(store.FindDoc(id) is { } doc ? " — " + doc.Title : "")}");
        }

        if (type.SampleIds is { Count: > 0 } sampleIds)
        {
            sb.AppendLine().AppendLine("## Gallery samples (get_samples)");
            foreach (var id in sampleIds)
                sb.AppendLine($"- {id}{(store.FindSample(id)?.Title is { } title ? " — " + title : "")}");
        }
    }

    private static IEnumerable<string> Categories(ServerState state) =>
        state.Store.Index.Types.Select(t => t.Category).OfType<string>().Distinct().Order(StringComparer.Ordinal);

    private static string KindName(TypeKind kind) => kind switch
    {
        TypeKind.Control => "control",
        TypeKind.Element => "element",
        TypeKind.Object => "object",
        TypeKind.Helper => "attached-property helper",
        TypeKind.MarkupExtension => "markup extension",
        TypeKind.Converter => "value converter",
        TypeKind.ResourceDictionary => "resource dictionary",
        TypeKind.Enum => "enum",
        _ => kind.ToString(),
    };
}
