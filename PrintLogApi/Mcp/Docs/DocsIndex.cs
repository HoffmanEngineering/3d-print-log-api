using System.Text.RegularExpressions;

namespace PrintLogApi.Mcp.Docs;

/// <summary>One docs page as the index describes it.</summary>
public sealed record DocsPage(
    string Slug,
    string Title,
    string Description,
    string Section,
    string Url,
    string MarkdownUrl)
{
    /// <summary>The MCP resource URI of this page.</summary>
    public string ResourceUri => DocsIndex.ResourceUriPrefix + Slug;
}

/// <summary>A page and its Markdown body.</summary>
public sealed record DocsDocument(DocsPage Page, string Markdown);

/// <summary>
/// Reads <c>/docs/llms.txt</c>, the index the UI's <c>scripts/docs-twins.mjs</c> generates. Each
/// page is one line, <c>- [Label](https://www.3dprintlog.com/docs/slug.md): Description</c>,
/// under a <c>## Section</c> heading.
/// </summary>
public static partial class DocsIndex
{
    public const string ResourceUriPrefix = "docs://";

    private const int MaxTitleLength = 200;
    private const int MaxDescriptionLength = 1000;

    /// <summary>
    /// A docs slug: lowercase letters and digits in hyphen-separated runs. Each run must hold at
    /// least one character and runs are split by exactly one hyphen, so there is one way to match.
    /// <c>\z</c>, not <c>$</c>: <c>$</c> also matches before a trailing newline.
    /// </summary>
    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*\z")]
    private static partial Regex SlugRule();

    public static bool IsValidSlug(string? slug) =>
        !string.IsNullOrEmpty(slug) && slug.Length <= 100 && SlugRule().IsMatch(slug);

    /// <summary>
    /// The pages the index lists. A line is kept only when its link is exactly
    /// <c>{origin}/docs/{slug}.md</c> with a valid slug, so the index cannot point a fetch at
    /// another host, path or query. Duplicates keep their first occurrence.
    /// </summary>
    public static IReadOnlyList<DocsPage> Parse(string text, Uri origin, int maxPages)
    {
        var pages = new List<DocsPage>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var section = "";

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                section = line[3..].Trim();
                continue;
            }

            if (!line.StartsWith("- [", StringComparison.Ordinal))
            {
                continue;
            }

            var labelEnd = line.IndexOf("](", 3, StringComparison.Ordinal);
            if (labelEnd < 0)
            {
                continue;
            }

            var urlEnd = line.IndexOf(')', labelEnd + 2);
            if (urlEnd < 0)
            {
                continue;
            }

            var link = line[(labelEnd + 2)..urlEnd];
            if (!TryGetSlug(link, origin, out var slug) || !seen.Add(slug))
            {
                continue;
            }

            var title = Truncate(line[3..labelEnd].Trim(), MaxTitleLength);
            var rest = line[(urlEnd + 1)..];
            var description = rest.StartsWith(':') ? Truncate(rest[1..].Trim(), MaxDescriptionLength) : "";

            pages.Add(new DocsPage(
                slug,
                title.Length > 0 ? title : slug,
                description,
                section,
                PageUrl(origin, slug),
                MarkdownUrl(origin, slug)));

            if (pages.Count >= maxPages)
            {
                break;
            }
        }

        return pages;
    }

    /// <summary>The HTML page for a slug.</summary>
    public static string PageUrl(Uri origin, string slug) =>
        $"{origin.GetLeftPart(UriPartial.Authority)}/docs/{slug}";

    /// <summary>The Markdown twin for a slug, which is what gets fetched.</summary>
    public static string MarkdownUrl(Uri origin, string slug) => PageUrl(origin, slug) + ".md";

    /// <summary>The slug in a <c>docs://slug</c> resource URI, or null when it is not one.</summary>
    public static string? SlugFromResourceUri(string? uri)
    {
        if (uri is null || !uri.StartsWith(ResourceUriPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var slug = uri[ResourceUriPrefix.Length..];
        return IsValidSlug(slug) ? slug : null;
    }

    private static bool TryGetSlug(string link, Uri origin, out string slug)
    {
        slug = "";
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != origin.Port
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        const string prefix = "/docs/";
        const string suffix = ".md";
        var path = uri.AbsolutePath;
        if (!path.StartsWith(prefix, StringComparison.Ordinal)
            || !path.EndsWith(suffix, StringComparison.Ordinal)
            || path.Length <= prefix.Length + suffix.Length)
        {
            return false;
        }

        var candidate = path[prefix.Length..^suffix.Length];
        if (!IsValidSlug(candidate))
        {
            return false;
        }

        slug = candidate;
        return true;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
