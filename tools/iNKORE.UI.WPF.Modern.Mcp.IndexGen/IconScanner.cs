using System.Text.RegularExpressions;
using iNKORE.UI.WPF.Modern.Mcp.Index;

namespace iNKORE.UI.WPF.Modern.Mcp.IndexGen;

/// <summary>
/// Reads the generated icon key classes (Common/IconKeys/*.cs). Their glyph values are static
/// initializers, which metadata cannot see, so the source is parsed instead.
/// </summary>
internal static partial class IconScanner
{
    public static List<IconSet> Scan(string iconKeysDirectory, string xmlPrefix)
    {
        var sets = new Dictionary<string, IconSet>(StringComparer.Ordinal);

        foreach (var file in Directory.GetFiles(iconKeysDirectory, "*.cs").Order(StringComparer.Ordinal))
        {
            var text = File.ReadAllText(file);
            if (ClassRegex().Match(text) is not { Success: true } classMatch)
                continue;

            var className = classMatch.Groups[1].Value;
            foreach (Match icon in IconFieldRegex().Matches(text))
            {
                if (!sets.TryGetValue(className, out var set))
                {
                    set = new IconSet { Name = className, Usage = $"{{x:Static {xmlPrefix}:{className}.{{0}}}}" };
                    sets.Add(className, set);
                }

                set.Icons.TryAdd(icon.Groups[1].Value, icon.Groups[2].Value.ToUpperInvariant());
            }
        }

        return sets.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    [GeneratedRegex(@"static\s+partial\s+class\s+(\w+)")]
    private static partial Regex ClassRegex();

    [GeneratedRegex(@"public\s+static\s+readonly\s+FontIconData\s+(\w+)\s*=\s*CreateIcon\(\s*0x([0-9A-Fa-f]+)")]
    private static partial Regex IconFieldRegex();
}
