using System.ComponentModel;
using ModelContextProtocol.Server;

namespace PrintLogApi.Mcp.Docs;

/// <summary>
/// The public documentation, as tools. Served on both endpoints: anonymously on
/// <c>/mcp/docs</c>, and on <c>/mcp</c> so a signed-in agent needs no second connection.
///
/// <para>No <c>[Authorize]</c>, on purpose: the docs are public. That is safe on <c>/mcp</c>
/// because the endpoint policy already requires a token there, and it is the reason this class
/// must never take a user-scoped service. The data tools are kept off <c>/mcp/docs</c>
/// structurally (see <c>McpDocsEndpoint</c>) and by their own class-level policies.</para>
/// </summary>
[McpServerToolType]
[PublicDocsTools]
public class PrintLogDocsTools(IDocsCatalog catalog)
{
    private const int DefaultLimit = 5;
    private const int MaxLimit = 20;

    internal const string UnavailableMessage =
        "The documentation could not be loaded right now. Try again in a few minutes, or read it at https://www.3dprintlog.com/docs.";

    [McpServerTool(Title = "Search Docs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Search the 3D Print Log user documentation: how to use the app and its integrations " +
        "(Klipper, OctoPrint, Cura, slicer uploaders, the Android app, the MCP server, the REST " +
        "API), what Pro includes, and account and privacy questions. Returns the best-matching " +
        "pages first, each with a short excerpt, its slug and its docs:// resource URI. Read a " +
        "whole page with get_doc(slug). This searches documentation only, never the user's own " +
        "prints or materials.")]
    public async Task<DocsSearchResult> SearchDocs(
        [Description("Keywords or a question, e.g. 'klipper webhook' or 'what does Pro include'.")] string query,
        [Description("Most results to return (default 5, max 20).")] int? limit = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > DocsSearch.MaxQueryLength)
        {
            throw McpToolException.InvalidArguments(
                $"'query' must be between 1 and {DocsSearch.MaxQueryLength} characters.");
        }

        var terms = DocsSearch.Terms(query);
        if (terms.Count == 0)
        {
            throw McpToolException.InvalidArguments("'query' has no searchable words. Use a topic, e.g. 'klipper'.");
        }

        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var corpus = await RequireCorpus(ct);
        var matches = DocsSearch.Search(corpus.Documents, terms);

        return new DocsSearchResult(
            query,
            matches.Take(take).Select(m => DocsSearch.ToHit(m.Document, m.Excerpt)).ToList(),
            matches.Count);
    }

    [McpServerTool(Title = "List Docs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "List every page of the 3D Print Log user documentation with its slug, title, summary " +
        "and section. Use it to browse; use search_docs to find a topic.")]
    public async Task<IReadOnlyList<DocsListItem>> ListDocs(CancellationToken ct = default)
    {
        var corpus = await RequireCorpus(ct);
        return corpus.Documents
            .Select(d => new DocsListItem(
                d.Page.Slug, d.Page.Title, d.Page.Description, d.Page.Section, d.Page.ResourceUri, d.Page.Url))
            .ToList();
    }

    [McpServerTool(Title = "Get Doc", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Read one page of the 3D Print Log user documentation, as Markdown, by its slug (from " +
        "search_docs or list_docs, e.g. 'klipper'). The page starts with its title, summary and " +
        "the URL of its HTML version; cite that URL when quoting it.")]
    public async Task<string> GetDoc(
        [Description("The page slug, e.g. 'klipper' or 'pro-subscription'.")] string slug,
        CancellationToken ct = default)
    {
        if (!DocsIndex.IsValidSlug(slug))
        {
            throw McpToolException.InvalidArguments(
                "'slug' is lowercase letters, digits and hyphens, e.g. 'klipper'. Find one with search_docs.");
        }

        var corpus = await RequireCorpus(ct);
        return corpus.Find(slug)?.Markdown
            ?? throw McpToolException.NotFound($"No docs page has the slug '{slug}'. Find one with search_docs or list_docs.");
    }

    private async Task<DocsCorpus> RequireCorpus(CancellationToken ct)
    {
        var corpus = await catalog.GetCorpusAsync(ct);
        return corpus.Available ? corpus : throw McpToolException.Unavailable(UnavailableMessage);
    }
}
