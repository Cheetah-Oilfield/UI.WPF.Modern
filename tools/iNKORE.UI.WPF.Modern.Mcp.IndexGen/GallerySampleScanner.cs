using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using iNKORE.UI.WPF.Modern.Mcp.Index;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace iNKORE.UI.WPF.Modern.Mcp.IndexGen;

internal sealed record GalleryItem(string UniqueId, string Title, string Category, string? Summary);

/// <summary>
/// Reads the Gallery's control catalog (DataModel/Data/Controls.*.json) and the example code
/// each page shows (ExampleNXaml / ExampleNCS string properties assigned to ControlExample).
/// </summary>
internal sealed partial class GallerySampleScanner
{
    private const char HoleStart = '\u0001';
    private const char HoleEnd = '\u0002';

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    public List<GalleryItem> ReadCatalog(string galleryDirectory)
    {
        var items = new List<GalleryItem>();
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

        foreach (var file in Directory.GetFiles(Path.Combine(galleryDirectory, "DataModel", "Data"), "Controls.*.json").Order(StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file), options);
            if (!doc.RootElement.TryGetProperty("Groups", out var groups))
                continue;

            foreach (var group in groups.EnumerateArray())
            {
                var category = group.GetProperty("Title").GetString() ?? "";
                if (!group.TryGetProperty("Items", out var groupItems))
                    continue;

                foreach (var item in groupItems.EnumerateArray())
                {
                    var id = item.GetProperty("UniqueId").GetString();
                    if (string.IsNullOrEmpty(id))
                        continue;

                    var subtitle = item.TryGetProperty("Subtitle", out var s) ? s.GetString() : null;
                    items.Add(new GalleryItem(id, item.GetProperty("Title").GetString() ?? id, category, string.IsNullOrWhiteSpace(subtitle) ? null : subtitle.Trim()));
                }
            }
        }

        return items;
    }

    public List<SampleEntry> ReadSamples(string galleryDirectory, IEnumerable<GalleryItem> catalog)
    {
        var pages = Directory.GetFiles(Path.Combine(galleryDirectory, "Pages"), "*Page.xaml", SearchOption.AllDirectories)
            .GroupBy(p => Path.GetFileNameWithoutExtension(p)[..^"Page".Length], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var samples = new List<SampleEntry>();
        foreach (var item in catalog)
        {
            if (!pages.TryGetValue(item.UniqueId, out var xamlPath) || !File.Exists(xamlPath + ".cs"))
                continue;

            samples.AddRange(ReadPage(item.UniqueId, xamlPath, xamlPath + ".cs"));
        }

        return samples;
    }

    private static IEnumerable<SampleEntry> ReadPage(string galleryId, string xamlPath, string codePath)
    {
        var headers = ReadExampleHeaders(xamlPath);
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(codePath)).GetRoot();

        var strings = new Dictionary<string, ExpressionSyntax>(StringComparer.Ordinal);
        foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            var expression = property.ExpressionBody?.Expression
                ?? property.AccessorList?.Accessors
                    .FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration))?
                    .DescendantNodes().OfType<ReturnStatementSyntax>().FirstOrDefault()?.Expression;
            if (expression is not null)
                strings.TryAdd(property.Identifier.ValueText, expression);
        }

        // ExampleN.Xaml = ExampleNXaml; ExampleN.CSharp = ExampleNCS;
        var examples = new Dictionary<string, (string? Xaml, string? CSharp)>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Left is not MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax target, Name.Identifier.ValueText: var member }
                || member is not ("Xaml" or "CSharp")
                || assignment.Right is not IdentifierNameSyntax source
                || !strings.TryGetValue(source.Identifier.ValueText, out var expression))
            {
                continue;
            }

            var name = target.Identifier.ValueText;
            if (!examples.TryGetValue(name, out var code))
                order.Add(name);

            var text = Evaluate(expression);
            examples[name] = member == "Xaml" ? (text ?? code.Xaml, code.CSharp) : (code.Xaml, text ?? code.CSharp);
        }

        // Keep the order the examples appear on the page.
        var pageOrder = headers.Keys.ToList();
        foreach (var name in order.OrderBy(n => pageOrder.IndexOf(n) is >= 0 and var i ? i : int.MaxValue))
        {
            var (rawXaml, rawCSharp) = examples[name];
            var xaml = rawXaml is null ? null : Dedent(CleanXaml(rawXaml));
            var csharp = rawCSharp is null ? null : Dedent(CleanCSharp(rawCSharp));
            if (string.IsNullOrWhiteSpace(xaml) && string.IsNullOrWhiteSpace(csharp))
                continue;

            yield return new SampleEntry
            {
                Id = $"{galleryId}/{name}",
                GalleryId = galleryId,
                Title = headers.GetValueOrDefault(name),
                Xaml = string.IsNullOrWhiteSpace(xaml) ? null : xaml,
                CSharp = string.IsNullOrWhiteSpace(csharp) ? null : csharp,
                Elements = xaml is null ? null : ElementNames(xaml),
            };
        }
    }

    private static Dictionary<string, string?> ReadExampleHeaders(string xamlPath)
    {
        var headers = new Dictionary<string, string?>(StringComparer.Ordinal);
        try
        {
            foreach (var element in XDocument.Load(xamlPath).Descendants().Where(e => e.Name.LocalName == "ControlExample"))
            {
                if ((string?)element.Attribute(X + "Name") is { } name)
                    headers.TryAdd(name, (string?)element.Attribute("HeaderText"));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: could not parse {xamlPath}: {ex.Message}");
        }

        return headers;
    }

    /// <summary>Evaluates a string literal, interpolated string or concatenation; interpolation holes become markers.</summary>
    private static string? Evaluate(ExpressionSyntax expression)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                return literal.Token.ValueText;

            case InterpolatedStringExpressionSyntax interpolated:
                var sb = new StringBuilder();
                foreach (var content in interpolated.Contents)
                {
                    // ValueText unescapes "" but leaves the doubled braces of interpolated strings.
                    if (content is InterpolatedStringTextSyntax text)
                        sb.Append(text.TextToken.ValueText.Replace("{{", "{").Replace("}}", "}"));
                    else if (content is InterpolationSyntax hole)
                        sb.Append(HoleStart).Append(hole.Expression.ToString()).Append(HoleEnd);
                }

                return sb.ToString();

            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression):
                var left = Evaluate(binary.Left);
                var right = Evaluate(binary.Right);
                return left is null || right is null ? null : left + right;

            case ParenthesizedExpressionSyntax parenthesized:
                return Evaluate(parenthesized.Expression);

            default:
                return null;
        }
    }

    // Attributes whose whole value is a runtime interpolation are dropped; other holes become
    // the expression text (inside a tag) or an XML comment (in content).
    private static string CleanXaml(string xaml)
    {
        xaml = InterpolatedAttributeRegex().Replace(xaml, "");

        var sb = new StringBuilder();
        for (var i = 0; i < xaml.Length; i++)
        {
            if (xaml[i] != HoleStart)
            {
                sb.Append(xaml[i]);
                continue;
            }

            var end = xaml.IndexOf(HoleEnd, i);
            var expression = xaml[(i + 1)..end];
            var insideTag = xaml.LastIndexOf('<', i) > xaml.LastIndexOf('>', i);
            sb.Append(insideTag ? expression : $"<!-- {expression} -->");
            i = end;
        }

        return sb.ToString();
    }

    private static string CleanCSharp(string code) =>
        HoleRegex().Replace(code, m => $"/* {m.Groups[1].Value} */");

    private static string Dedent(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
            lines.RemoveAt(0);
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
            lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0)
            return "";

        var indent = lines.Where(l => l.Trim().Length > 0).Min(l => l.Length - l.TrimStart().Length);
        return string.Join('\n', lines.Select(l => l.Length >= indent ? l[indent..].TrimEnd() : l.TrimEnd()));
    }

    private static List<string>? ElementNames(string xaml)
    {
        var names = ElementNameRegex().Matches(xaml)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        return names.Count == 0 ? null : names;
    }

    [GeneratedRegex("\\s+[\\w:.]+\\s*=\\s*\"\u0001[^\u0002]*\u0002\"")]
    private static partial Regex InterpolatedAttributeRegex();

    [GeneratedRegex("\u0001([^\u0002]*)\u0002")]
    private static partial Regex HoleRegex();

    [GeneratedRegex(@"<(?:\w+:)?([A-Z]\w*)(?=[\s/>])")]
    private static partial Regex ElementNameRegex();
}
