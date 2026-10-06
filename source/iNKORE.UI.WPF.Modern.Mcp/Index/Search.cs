using System.Text;

namespace iNKORE.UI.WPF.Modern.Mcp.Index;

internal static class Tokenizer
{
    // WinUI/WPF resource names say "PointerOver" where people say "hover", and so on.
    private static readonly Dictionary<string, string[]> Synonyms = new(StringComparer.Ordinal)
    {
        ["hover"] = ["pointer", "over"],
        ["mouseover"] = ["pointer", "over"],
        ["click"] = ["pressed"],
        ["press"] = ["pressed"],
        ["colour"] = ["color"],
        ["grey"] = ["gray"],
        ["bg"] = ["background"],
        ["hamburger"] = ["global", "nav"],
        ["fg"] = ["foreground"],
    };

    /// <summary>Splits on non-alphanumerics and camelCase/digit boundaries, lowercased.</summary>
    public static List<string> Split(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0)
                tokens.Add(current.ToString().ToLowerInvariant());
            current.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }

            if (current.Length > 0)
            {
                var previous = text[i - 1];
                var boundary =
                    (char.IsUpper(c) && char.IsLower(previous))
                    || (char.IsUpper(c) && i + 1 < text.Length && char.IsLower(text[i + 1]) && char.IsUpper(previous))
                    || (char.IsDigit(c) != char.IsDigit(previous));
                if (boundary)
                    Flush();
            }

            current.Append(c);
        }

        Flush();
        return tokens;
    }

    /// <summary>Query terms: each token, expanded by synonyms.</summary>
    public static List<string[]> QueryTerms(string query) =>
        Split(query).Select(t => Synonyms.TryGetValue(t, out var expanded) ? expanded.Prepend(t).ToArray() : [t]).ToList();
}

/// <summary>Ranks names (types, resource keys, icon names) against a free-text query.</summary>
internal static class NameSearch
{
    public static IEnumerable<T> Rank<T>(IEnumerable<T> items, Func<T, string> name, string query)
    {
        var terms = Tokenizer.QueryTerms(query);
        if (terms.Count == 0)
            return [];

        var compactQuery = string.Concat(Tokenizer.Split(query));
        return items
            .Select(item => (Item: item, Name: name(item), Score: Score(name(item), terms, compactQuery)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Name.Length)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => x.Item);
    }

    private static double Score(string name, List<string[]> terms, string compactQuery)
    {
        var lower = name.ToLowerInvariant();
        if (lower == compactQuery)
            return 1000;

        var tokens = Tokenizer.Split(name);
        double score = 0;
        var matched = 0;
        foreach (var alternatives in terms)
        {
            double best = 0;
            foreach (var term in alternatives)
            {
                if (tokens.Contains(term))
                    best = Math.Max(best, 3);
                else if (tokens.Any(t => t.StartsWith(term, StringComparison.Ordinal)))
                    best = Math.Max(best, 2);
                else if (lower.Contains(term, StringComparison.Ordinal))
                    best = Math.Max(best, 1);
            }

            if (best > 0)
                matched++;
            score += best;
        }

        if (matched == 0)
            return 0;

        // Every query term matching dominates partial matches; a name that starts with the query ranks higher.
        score += matched == terms.Count ? 100 : 0;
        if (lower.StartsWith(compactQuery, StringComparison.Ordinal))
            score += 20;
        return score;
    }
}

/// <summary>BM25 over doc sections, with headings and titles weighted above body text.</summary>
public sealed class SectionSearch
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    private readonly List<(DocArticle Doc, DocSection Section, Dictionary<string, double> Terms, double Length)> _entries = [];
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);
    private readonly double _averageLength;

    public SectionSearch(IEnumerable<DocArticle> docs)
    {
        foreach (var doc in docs)
        {
            foreach (var section in doc.Sections)
            {
                var terms = new Dictionary<string, double>(StringComparer.Ordinal);
                void Add(string text, double weight)
                {
                    foreach (var token in Tokenizer.Split(text))
                        terms[token] = terms.GetValueOrDefault(token) + weight;
                }

                Add(doc.Title, 3);
                Add(section.Heading, 3);
                Add(doc.Description ?? "", 1);
                Add(section.Markdown, 1);

                foreach (var term in terms.Keys)
                    _documentFrequency[term] = _documentFrequency.GetValueOrDefault(term) + 1;
                _entries.Add((doc, section, terms, terms.Values.Sum()));
            }
        }

        _averageLength = _entries.Count == 0 ? 1 : _entries.Average(e => e.Length);
    }

    public IEnumerable<(DocArticle Doc, DocSection Section, double Score)> Search(string query)
    {
        var terms = Tokenizer.QueryTerms(query);
        var n = _entries.Count;

        return _entries
            .Select(e =>
            {
                double score = 0;
                foreach (var alternatives in terms)
                {
                    double best = 0;
                    foreach (var term in alternatives)
                    {
                        if (!e.Terms.TryGetValue(term, out var tf))
                            continue;
                        var df = _documentFrequency[term];
                        var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                        best = Math.Max(best, idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * e.Length / _averageLength)));
                    }

                    score += best;
                }

                return (e.Doc, e.Section, Score: score);
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score);
    }
}
