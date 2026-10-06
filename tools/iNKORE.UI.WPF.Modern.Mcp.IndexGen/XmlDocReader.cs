using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace iNKORE.UI.WPF.Modern.Mcp.IndexGen;

/// <summary>
/// Reads compiler-generated XML documentation files and returns plain-text summaries by member ID.
/// </summary>
internal sealed partial class XmlDocReader
{
    private readonly Dictionary<string, string> _summaries = new(StringComparer.Ordinal);

    // Methods are looked up without their parameter list (e.g. "M:Ns.Type.GetFoo").
    private readonly Dictionary<string, string> _methodSummaries = new(StringComparer.Ordinal);

    public void Load(string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"warning: XML doc file not found: {path}");
            return;
        }

        var doc = XDocument.Load(path);
        foreach (var member in doc.Descendants("member"))
        {
            var name = (string?)member.Attribute("name");
            var summary = member.Element("summary");
            if (name is null || summary is null)
                continue;

            var text = ToPlainText(summary);
            if (text.Length == 0)
                continue;

            _summaries.TryAdd(name, text);

            if (name.StartsWith("M:", StringComparison.Ordinal))
            {
                var paren = name.IndexOf('(');
                _methodSummaries.TryAdd(paren < 0 ? name : name[..paren], text);
            }
        }
    }

    public string? Type(string fullName) => Get("T:" + fullName);

    public string? Property(string typeFullName, string name) => Get($"P:{typeFullName}.{name}");

    public string? Field(string typeFullName, string name) => Get($"F:{typeFullName}.{name}");

    public string? Event(string typeFullName, string name) => Get($"E:{typeFullName}.{name}");

    public string? Method(string typeFullName, string name) =>
        _methodSummaries.GetValueOrDefault($"M:{typeFullName}.{name}");

    private string? Get(string id) => _summaries.GetValueOrDefault(id);

    private static string ToPlainText(XElement element)
    {
        var sb = new StringBuilder();
        Append(element, sb);
        return WhitespaceRegex().Replace(sb.ToString(), " ").Trim();
    }

    private static void Append(XNode node, StringBuilder sb)
    {
        switch (node)
        {
            case XText text:
                sb.Append(text.Value);
                break;

            case XElement { Name.LocalName: "see" or "seealso" } see:
                var cref = (string?)see.Attribute("cref") ?? (string?)see.Attribute("langword") ?? (string?)see.Attribute("href");
                if (!see.IsEmpty)
                    sb.Append(see.Value);
                else if (cref is not null)
                    sb.Append('`').Append(ShortCref(cref)).Append('`');
                break;

            case XElement { Name.LocalName: "paramref" or "typeparamref" } paramRef:
                sb.Append('`').Append((string?)paramRef.Attribute("name")).Append('`');
                break;

            case XElement { Name.LocalName: "c" } code:
                sb.Append('`').Append(code.Value).Append('`');
                break;

            case XElement { Name.LocalName: "para" } para:
                sb.Append(' ');
                foreach (var child in para.Nodes())
                    Append(child, sb);
                sb.Append(' ');
                break;

            case XElement other:
                foreach (var child in other.Nodes())
                    Append(child, sb);
                break;
        }
    }

    // "T:iNKORE.UI.WPF.Modern.Controls.NavigationView" -> "NavigationView"
    // "P:iNKORE.UI.WPF.Modern.Controls.NavigationView.IsPaneOpen" -> "NavigationView.IsPaneOpen"
    private static string ShortCref(string cref)
    {
        var colon = cref.IndexOf(':');
        var body = colon == 1 ? cref[2..] : cref;
        var paren = body.IndexOf('(');
        if (paren >= 0)
            body = body[..paren];

        var parts = body.Split('.');
        return cref.StartsWith("T:", StringComparison.Ordinal) || parts.Length < 2
            ? parts[^1]
            : $"{parts[^2]}.{parts[^1]}";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
