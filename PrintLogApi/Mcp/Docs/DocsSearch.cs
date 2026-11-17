using System.Text;

namespace PrintLogApi.Mcp.Docs;

/// <summary>One page matching a <c>search_docs</c> query.</summary>
public sealed record DocsSearchHit(
    string Slug,
    string Title,
    string Description,
    string Section,
    string ResourceUri,
    string Url,
    string MarkdownUrl,
    string Excerpt);

/// <summary>The result of <c>search_docs</c>, best match first.</summary>
public sealed record DocsSearchResult(string Query, IReadOnlyList<DocsSearchHit> Results, int TotalMatches);

/// <summary>A docs page in the <c>list_docs</c> result.</summary>
public sealed record DocsListItem(
    string Slug,
    string Title,
    string Description,
    string Section,
    string ResourceUri,
    string Url);

/// <summary>
/// Keyword search over a couple of dozen pages. Small enough to score in memory on every call;
/// an index would only add a second thing to keep in step with the corpus.
/// </summary>
public static class DocsSearch
{
    public const int MaxQueryLength = 200;
    private const int MaxTerms = 8;
    private const int ExcerptBefore = 80;
    private const int ExcerptAfter = 220;

    /// <summary>
    /// Words that appear in nearly every question ("how do I connect Klipper?") and on nearly
    /// every page, so they would rank by page length rather than by topic.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from", "how",
        "i", "in", "is", "it", "me", "my", "of", "on", "or", "that", "the", "this", "to", "what",
        "when", "where", "which", "why", "with", "you", "your",
    };

    /// <summary>The distinct, lowercased search terms in a query, stop words removed.</summary>
    public static IReadOnlyList<string> Terms(string query)
    {
        var terms = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length == 0)
            {
                return;
            }
            var term = current.ToString();
            current.Clear();
            if (!StopWords.Contains(term) && !terms.Contains(term) && terms.Count < MaxTerms)
            {
                terms.Add(term);
            }
        }

        foreach (var ch in query)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                Flush();
            }
        }
        Flush();

        return terms;
    }

    public static IReadOnlyList<(DocsDocument Document, string Excerpt)> Search(
        IReadOnlyList<DocsDocument> documents, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0 || documents.Count == 0)
        {
            return [];
        }

        // A term found on every page says little about which page is meant; weight each term by
        // how few pages carry it.
        var weights = terms.ToDictionary(
            t => t,
            t =>
            {
                var df = documents.Count(d => Contains(d, t));
                return df == 0 ? 0 : Math.Log(1 + (double)documents.Count / df);
            });

        var scored = new List<(DocsDocument Document, double Score)>();
        foreach (var document in documents)
        {
            double score = 0;
            foreach (var term in terms)
            {
                var weight = weights[term];
                if (weight == 0)
                {
                    continue;
                }

                var inTitle = document.Page.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || document.Page.Slug.Contains(term, StringComparison.Ordinal);
                var inDescription = document.Page.Description.Contains(term, StringComparison.OrdinalIgnoreCase);
                var bodyCount = CountOccurrences(document.Markdown, term, cap: 10);
                if (!inTitle && !inDescription && bodyCount == 0)
                {
                    continue;
                }

                score += weight * (1 + (inTitle ? 3 : 0) + (inDescription ? 2 : 0) + bodyCount / 10.0);
            }

            if (score > 0)
            {
                scored.Add((document, score));
            }
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Document.Page.Slug, StringComparer.Ordinal)
            .Select(s => (s.Document, Excerpt(s.Document, terms)))
            .ToList();
    }

    public static DocsSearchHit ToHit(DocsDocument document, string excerpt) => new(
        document.Page.Slug,
        document.Page.Title,
        document.Page.Description,
        document.Page.Section,
        document.Page.ResourceUri,
        document.Page.Url,
        document.Page.MarkdownUrl,
        excerpt);

    private static bool Contains(DocsDocument document, string term) =>
        document.Page.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
        || document.Page.Slug.Contains(term, StringComparison.Ordinal)
        || document.Page.Description.Contains(term, StringComparison.OrdinalIgnoreCase)
        || document.Markdown.Contains(term, StringComparison.OrdinalIgnoreCase);

    private static int CountOccurrences(string text, string term, int cap)
    {
        var count = 0;
        var index = 0;
        while (count < cap && (index = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += term.Length;
        }
        return count;
    }

    /// <summary>
    /// Text around the first place in the page body a term appears, or the description when the
    /// match was only in the title. The twin's preamble (title, summary, HTML link) repeats the
    /// index entry, so the search starts after it.
    /// </summary>
    private static string Excerpt(DocsDocument document, IReadOnlyList<string> terms)
    {
        var text = document.Markdown;
        var bodyStart = text.IndexOf("\nHTML version:", StringComparison.Ordinal);
        bodyStart = bodyStart < 0 ? 0 : Math.Max(0, text.IndexOf('\n', bodyStart + 1));

        var first = -1;
        foreach (var term in terms)
        {
            var at = text.IndexOf(term, bodyStart, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && (first < 0 || at < first))
            {
                first = at;
            }
        }

        if (first < 0)
        {
            return document.Page.Description;
        }

        var start = Math.Max(bodyStart, first - ExcerptBefore);
        var end = Math.Min(text.Length, first + ExcerptAfter);
        var excerpt = CollapseWhitespace(text[start..end]);
        return (start > bodyStart ? "…" : "") + excerpt + (end < text.Length ? "…" : "");
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(ch);
        }
        return builder.ToString();
    }
}
