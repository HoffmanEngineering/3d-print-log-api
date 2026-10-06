using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PrintLogApi.Mcp.Docs;

namespace PrintLogApi.Mcp.Apps;

/// <summary>
/// MCP Apps views (api#130): <c>ui://</c> resources a host renders in a sandboxed iframe in place
/// of a tool's text result.
///
/// <para>Built against MCP Apps (SEP-1865), stable spec version <see cref="SpecVersion"/>:
/// https://github.com/modelcontextprotocol/ext-apps/blob/main/specification/2026-01-26/apps.mdx.
/// A tool links to its view with <c>_meta.ui.resourceUri</c>, plus ChatGPT's
/// <c>openai/outputTemplate</c> alias. The deprecated flat <c>_meta["ui/resourceUri"]</c> key is
/// deliberately not emitted: the spec drops it before GA.</para>
///
/// <para><b>A view is presentation only.</b> The tool's text result is unchanged, because most
/// clients do not render apps, and the view renders that same result. No view calls a tool, so
/// no view can start a write. The confirm-before-write half of #130 was declined for that reason;
/// see the PR for why.</para>
///
/// <para><b>Views exist only on <c>/mcp</c>.</b> They carry no user data (the HTML is a static
/// template; the data arrives from the host over postMessage), but they belong to tools the
/// anonymous <c>/mcp/docs</c> endpoint does not serve, so that endpoint neither lists nor reads
/// them.</para>
///
/// <para>These handlers wrap the docs handlers rather than replacing them: the SDK takes one
/// list and one read handler per server, shared by both endpoints.</para>
/// </summary>
public static class McpAppResources
{
    /// <summary>The MCP Apps spec version these views and the metadata below were built against.</summary>
    public const string SpecVersion = "2026-01-26";

    /// <summary>The MIME type SEP-1865 requires for an HTML view.</summary>
    public const string MimeType = "text/html;profile=mcp-app";

    public const string Scheme = "ui://";

    /// <summary>The view for <c>get_material_inventory</c>.</summary>
    public const string InventoryUri = Scheme + "3d-print-log/material-inventory";

    /// <summary>The tool <c>_meta.ui</c> value linking get_material_inventory to its view.</summary>
    public const string InventoryToolUiMeta = "{\"resourceUri\":\"" + InventoryUri + "\"}";

    private sealed record AppView(string Uri, string Name, string Title, string Description, string ResourceName)
    {
        public Lazy<string> Html { get; } = new(() => LoadEmbedded(ResourceName));
    }

    private static readonly AppView[] Views =
    [
        new(
            InventoryUri,
            "material-inventory",
            "Material inventory",
            "Table view of get_material_inventory: remaining grams per spool, with material, color and storage location.",
            "PrintLogApi.Mcp.Apps.material-inventory.html"),
    ];

    /// <summary>The URIs of every view, for tests and for anything validating tool metadata.</summary>
    public static IReadOnlyList<string> Uris { get; } = Views.Select(v => v.Uri).ToArray();

    /// <summary>
    /// <c>_meta.ui</c> for a view: an explicitly empty CSP allowlist (no network, no external
    /// resources, no nested frames, no foreign base URI) and a host-drawn border. Spelled out
    /// rather than omitted so the policy is visible to a host reviewing the server, and so a
    /// host whose default is laxer than the spec's still gets the tight one. A fresh object each
    /// call, because a JsonNode can have only one parent.
    /// </summary>
    public static JsonObject UiMeta() => new()
    {
        ["ui"] = new JsonObject
        {
            ["csp"] = new JsonObject
            {
                ["connectDomains"] = new JsonArray(),
                ["resourceDomains"] = new JsonArray(),
                ["frameDomains"] = new JsonArray(),
                ["baseUriDomains"] = new JsonArray(),
            },
            ["prefersBorder"] = true,
        },
    };

    public static async ValueTask<ListResourcesResult> ListResources(
        RequestContext<ListResourcesRequestParams> request, CancellationToken cancellationToken)
    {
        var result = await McpDocsEndpoint.ListResources(request, cancellationToken);
        if (IsDocsEndpoint(request))
        {
            return result;
        }

        // The spec lets a server leave views out of resources/list, but listing them, with the
        // same _meta.ui as the read, lets a host review the CSP when it connects.
        var resources = result.Resources.ToList();
        resources.AddRange(Views.Select(v => new Resource
        {
            Uri = v.Uri,
            Name = v.Name,
            Title = v.Title,
            Description = v.Description,
            MimeType = MimeType,
            Meta = UiMeta(),
        }));
        result.Resources = resources;
        return result;
    }

    public static ValueTask<ReadResourceResult> ReadResource(
        RequestContext<ReadResourceRequestParams> request, CancellationToken cancellationToken)
    {
        var uri = request.Params?.Uri;
        if (uri is null || !uri.StartsWith(Scheme, StringComparison.Ordinal))
        {
            return McpDocsEndpoint.ReadResource(request, cancellationToken);
        }

        var view = IsDocsEndpoint(request)
            ? null
            : Views.FirstOrDefault(v => string.Equals(v.Uri, uri, StringComparison.Ordinal));
        if (view is null)
        {
            throw new McpProtocolException("Unknown resource.", McpErrorCode.ResourceNotFound);
        }

        return ValueTask.FromResult(new ReadResourceResult
        {
            Contents =
            [
                new TextResourceContents
                {
                    Uri = view.Uri,
                    MimeType = MimeType,
                    Text = view.Html.Value,
                    Meta = UiMeta(),
                },
            ],
        });
    }

    private static bool IsDocsEndpoint<T>(RequestContext<T> request)
    {
        // Fail closed: a request whose path cannot be seen is treated as the anonymous endpoint.
        var http = request.Services?.GetService<IHttpContextAccessor>()?.HttpContext;
        return http is null || McpDocsEndpoint.IsDocsRequest(http);
    }

    private static string LoadEmbedded(string resourceName)
    {
        using var stream = typeof(McpAppResources).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded MCP App view '{resourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
