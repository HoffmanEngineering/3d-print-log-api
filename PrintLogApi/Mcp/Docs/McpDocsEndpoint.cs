using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PrintLogApi.Mcp.Docs;

/// <summary>Marks a tool class whose tools are public documentation and are served on <see cref="McpDocsEndpoint.Path"/>.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PublicDocsToolsAttribute : Attribute;

/// <summary>
/// The anonymous docs endpoint, <c>/mcp/docs</c> (#129).
///
/// <para>A second endpoint rather than anonymous access on <c>/mcp</c>, because an MCP client
/// only starts OAuth when its first request is refused with a 401. Letting an unauthenticated
/// <c>initialize</c> through on <c>/mcp</c> so the docs tools could answer would leave a new
/// client connected, unauthenticated and shown only the docs, with nothing prompting it to sign
/// in. <c>/mcp</c> therefore keeps its endpoint policy and its exact 401.</para>
///
/// <para>Both endpoints share one MCP server registration. This endpoint narrows it per request
/// in <see cref="ConfigureSessionOptions"/>, which the SDK runs on a fresh options instance for
/// every stateless request, so the narrowing cannot leak into <c>/mcp</c>. The data tools are
/// removed from the collection here, not merely hidden: even with the filter gone, their own
/// <c>[Authorize]</c> policies would still refuse an anonymous caller.</para>
/// </summary>
public static class McpDocsEndpoint
{
    public const string Path = "/mcp/docs";

    /// <summary>The rate-limit policy for this endpoint, partitioned per client address.</summary>
    public const string RateLimitPolicy = "mcp-docs";

    public const string Instructions =
        "Public documentation for 3D Print Log (3dprintlog.com), a web app for logging 3D prints, " +
        "printers, filament and resin inventory, and projects. No sign-in needed. Use search_docs " +
        "to find a page, get_doc to read it, or read the docs:// resources. This server cannot see " +
        "anyone's prints or materials: for the user's own data, connect to " +
        "https://api.3dprintlog.com/mcp, which signs in with OAuth.";

    public static bool IsDocsRequest(HttpContext context) =>
        context.Request.Path.StartsWithSegments(Path);

    public static Task ConfigureSessionOptions(HttpContext context, McpServerOptions options, CancellationToken cancellationToken)
    {
        if (!IsDocsRequest(context))
        {
            return Task.CompletedTask;
        }

        var docsTools = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in options.ToolCollection ?? [])
        {
            if (tool.Metadata.OfType<PublicDocsToolsAttribute>().Any())
            {
                docsTools.Add(tool);
            }
        }

        options.ToolCollection = docsTools;
        options.ServerInstructions = Instructions;
        return Task.CompletedTask;
    }

    public static async ValueTask<ListResourcesResult> ListResources(
        RequestContext<ListResourcesRequestParams> request, CancellationToken cancellationToken)
    {
        var corpus = await Catalog(request).GetCorpusAsync(cancellationToken);
        return new ListResourcesResult
        {
            Resources = corpus.Documents.Select(d => new Resource
            {
                Uri = d.Page.ResourceUri,
                Name = d.Page.Slug,
                Title = d.Page.Title,
                Description = d.Page.Description,
                MimeType = "text/markdown",
            }).ToList(),
        };
    }

    public static ValueTask<ListResourceTemplatesResult> ListResourceTemplates(
        RequestContext<ListResourceTemplatesRequestParams> request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ListResourceTemplatesResult
        {
            ResourceTemplates =
            [
                new ResourceTemplate
                {
                    UriTemplate = DocsIndex.ResourceUriPrefix + "{slug}",
                    Name = "docs-page",
                    Title = "3D Print Log docs page",
                    Description = "A page of the 3D Print Log user documentation, as Markdown. Slugs come from resources/list or the search_docs tool.",
                    MimeType = "text/markdown",
                },
            ],
        });

    public static async ValueTask<ReadResourceResult> ReadResource(
        RequestContext<ReadResourceRequestParams> request, CancellationToken cancellationToken)
    {
        var uri = request.Params?.Uri;
        var slug = DocsIndex.SlugFromResourceUri(uri)
            ?? throw new McpProtocolException("Unknown resource. Docs resources are docs://<slug>.", McpErrorCode.ResourceNotFound);

        var corpus = await Catalog(request).GetCorpusAsync(cancellationToken);
        if (!corpus.Available)
        {
            throw new McpProtocolException(PrintLogDocsTools.UnavailableMessage, McpErrorCode.InternalError);
        }

        var document = corpus.Find(slug)
            ?? throw new McpProtocolException("No docs page has that slug.", McpErrorCode.ResourceNotFound);

        return new ReadResourceResult
        {
            Contents =
            [
                new TextResourceContents
                {
                    Uri = document.Page.ResourceUri,
                    MimeType = "text/markdown",
                    Text = document.Markdown,
                },
            ],
        };
    }

    private static IDocsCatalog Catalog<T>(RequestContext<T> request) =>
        request.Services?.GetRequiredService<IDocsCatalog>()
        ?? throw new InvalidOperationException("The MCP request has no service provider.");
}
