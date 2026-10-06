using System.Text;
using iNKORE.UI.WPF.Modern.Mcp.Index;

namespace iNKORE.UI.WPF.Modern.Mcp;

/// <summary>
/// The primer sent as MCP server instructions: the setup facts assistants most often get wrong,
/// plus a warning when the user's projects reference a different library version.
/// </summary>
public static class ServerInstructions
{
    public static string Build(IndexMetadata metadata, VersionCheckResult versionCheck)
    {
        var p = metadata.RecommendedPrefix;
        var sb = new StringBuilder();

        sb.AppendLine($"iNKORE.UI.WPF.Modern {metadata.LibraryVersion}: Fluent (WinUI-style) controls and theming for WPF.");
        sb.AppendLine("These tools answer from an index built from that exact library version. Prefer them over memory: many WinUI/UWP APIs do not exist in this WPF port or behave differently. Check with get_control before using a control or property you are not sure exists.");

        var warning = VersionWarning(metadata, versionCheck);
        if (warning is not null)
            sb.AppendLine().AppendLine(warning);

        sb.AppendLine();
        sb.AppendLine("Essentials:");
        sb.AppendLine($"- XAML namespace: xmlns:{p}=\"{metadata.XmlNamespace}\". Base helpers such as SimpleStackPanel come from the iNKORE.UI.WPF package: xmlns:ikw=\"http://schemas.inkore.net/lib/ui/wpf\" (not covered by this index).");
        sb.AppendLine($"- App.xaml must merge <{p}:ThemeResources/> and <{p}:XamlControlsResources/> into Application.Resources > ResourceDictionary.MergedDictionaries.");
        sb.AppendLine($"- Windows are not styled automatically: set {p}:WindowHelper.UseModernWindowStyle=\"True\" on each Window; backdrop via {p}:WindowHelper.SystemBackdropType (e.g. \"Mica\").");
        sb.AppendLine($"- Theme brushes and colors change with the theme: reference them with DynamicResource, never StaticResource or hard-coded colors. Keys are also exposed as {p}:ThemeKeys.<Key>Key, e.g. Foreground=\"{{DynamicResource {{x:Static {p}:ThemeKeys.TextFillColorPrimaryBrushKey}}}}\".");
        sb.AppendLine($"- Named styles: Style=\"{{StaticResource {{x:Static {p}:ThemeKeys.AccentButtonStyleKey}}}}\".");
        sb.AppendLine($"- To change a control's colors including hover/pressed states, override its lightweight resources (e.g. ButtonBackgroundPointerOver) in FrameworkElement.Resources or App.xaml with x:Key=\"{{x:Static {p}:ThemeKeys.ButtonBackgroundPointerOverKey}}\". Setting Background directly is undone by the template's state triggers. <{p}:StaticResource x:Key=\"...\" ResourceKey=\"...\"/> forwards one key to another. get_control lists a control's lightweight resources.");
        sb.AppendLine($"- Icons: <{p}:FontIcon Icon=\"{{x:Static {p}:SegoeFluentIcons.Save}}\"/>. Look names up with search_icons; do not guess them.");

        sb.AppendLine();
        sb.AppendLine("Tools: list_controls / get_control (what exists, properties, styles, lightweight resources), search_resources / get_resource (theme brushes and keys with per-theme values), search_docs / get_doc (official articles), get_samples (Gallery code), search_icons, get_server_info (versions).");
        return sb.ToString();
    }

    public static string? VersionWarning(IndexMetadata metadata, VersionCheckResult check)
    {
        if (check.Status != VersionStatus.Mismatch)
            return null;

        var versions = check.References.Select(r => r.Version).Where(v => v is not null).Distinct().ToList();
        return $"WARNING: this server describes iNKORE.UI.WPF.Modern {metadata.LibraryVersion}, but the project references {string.Join(", ", versions)}. "
            + "APIs and resource keys may differ; verify anything version-sensitive. Tell the user to install the matching server version if one is published: "
            + $"dotnet tool update -g iNKORE.UI.WPF.Modern.Mcp --version {versions.FirstOrDefault()}";
    }
}
