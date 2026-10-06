using System.Text;
using System.Text.RegularExpressions;
using iNKORE.UI.WPF.Modern.Mcp.Index;

namespace iNKORE.UI.WPF.Modern.Mcp.IndexGen;

/// <summary>
/// Reads the en-US articles from a checkout of the iNKORE-NET/Documentation repo and splits them into sections.
/// </summary>
internal static partial class DocsScanner
{
    public const string DocsRelativePath = "data/docs/ui-wpf-modern";
    private const string ArticleFileName = "index.en-US.mdx";

    public static List<DocArticle> Scan(string documentationRoot)
    {
        var docsRoot = Path.Combine(documentationRoot, DocsRelativePath);
        if (!Directory.Exists(docsRoot))
            throw new DirectoryNotFoundException($"Documentation content not found at {docsRoot}");

        return Directory.GetFiles(docsRoot, ArticleFileName, SearchOption.AllDirectories)
            .Select(path => ReadArticle(documentationRoot, docsRoot, path))
            .Where(a => !IsPlaceholder(a))
            .OrderBy(a => a.Id, StringComparer.Ordinal)
            .ToList();
    }

    // Unwritten articles contain only "We're still working on this, coming soon!".
    private static bool IsPlaceholder(DocArticle article)
    {
        var text = string.Concat(article.Sections.Select(s => s.Markdown));
        return PlaceholderRegex().Replace(text, "").Trim().Length == 0;
    }

    private static DocArticle ReadArticle(string documentationRoot, string docsRoot, string path)
    {
        var id = ToId(Path.GetRelativePath(docsRoot, Path.GetDirectoryName(path)!));
        var (frontMatter, body) = SplitFrontMatter(File.ReadAllText(path).Replace("\r\n", "\n"));
        body = MdxCommentRegex().Replace(body, "");

        var sections = new List<DocSection>();
        string? title = null;
        var heading = "";
        var level = 1;
        var current = new StringBuilder();
        var inFence = false;

        void Flush()
        {
            var markdown = current.ToString().Trim();
            if (markdown.Length > 0)
                sections.Add(new DocSection { Heading = heading, Level = level, Markdown = markdown });
            current.Clear();
        }

        foreach (var rawLine in body.Split('\n'))
        {
            // Docusaurus code-block markers, not code.
            if (HighlightMarkerRegex().IsMatch(rawLine))
                continue;

            // Site-relative links become doc:<id> so they can be followed with get_doc.
            var line = rawLine.Replace("%BASE_NAME%/", "doc:", StringComparison.Ordinal);

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                inFence = !inFence;

            if (!inFence)
            {
                if (title is null && line.StartsWith("# ", StringComparison.Ordinal))
                {
                    title = line[2..].Trim();
                    heading = title;
                    continue;
                }

                if (HeadingRegex().Match(line) is { Success: true } match)
                {
                    Flush();
                    heading = match.Groups[2].Value.Trim();
                    level = match.Groups[1].Value.Length;
                    continue;
                }

                // Screenshots carry no information for an assistant.
                if (ImageLineRegex().IsMatch(line))
                    continue;
            }

            current.Append(line).Append('\n');
        }

        Flush();

        title ??= frontMatter.GetValueOrDefault("title") ?? id.Split('/')[^1];
        return new DocArticle
        {
            Id = id,
            Title = title,
            Description = frontMatter.GetValueOrDefault("description"),
            SourcePath = Path.GetRelativePath(documentationRoot, path).Replace('\\', '/'),
            Sections = sections,
        };
    }

    // "03. components\01. basic-input\# button" -> "components/basic-input/button"
    private static string ToId(string relativeDirectory) =>
        string.Join('/', relativeDirectory
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Select(segment => SegmentPrefixRegex().Replace(segment, "").Trim().ToLowerInvariant().Replace(' ', '-')));

    private static (Dictionary<string, string> FrontMatter, string Body) SplitFrontMatter(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
            return (values, text);

        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
            return (values, text);

        foreach (var line in text[4..end].Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            var value = line[(colon + 1)..].Trim().Trim('"', '\'');
            if (value.Length > 0)
                values[line[..colon].Trim()] = value;
        }

        var bodyStart = text.IndexOf('\n', end + 4);
        return (values, bodyStart < 0 ? "" : text[(bodyStart + 1)..]);
    }

    [GeneratedRegex(@"^(#{2,4})\s+(.+)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*!\[[^\]]*\]\([^)]*\)\s*$")]
    private static partial Regex ImageLineRegex();

    [GeneratedRegex(@"\{/\*.*?\*/\}", RegexOptions.Singleline)]
    private static partial Regex MdxCommentRegex();

    [GeneratedRegex(@"We're still working on this,?\s*coming soon!?", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"^\s*(#|//|<!--)\s*highlight-(next-line|start|end)\s*(-->)?\s*$")]
    private static partial Regex HighlightMarkerRegex();

    [GeneratedRegex(@"^(\d+\.\s*)?(#\s*)?")]
    private static partial Regex SegmentPrefixRegex();
}
