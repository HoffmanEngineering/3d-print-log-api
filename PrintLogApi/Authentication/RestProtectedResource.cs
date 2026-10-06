using System.Text.Json.Serialization;
using PrintLogApi.Middleware;

namespace PrintLogApi.Authentication;

/// <summary>
/// RFC 9728 protected-resource metadata for the REST API, and the <c>resource_metadata</c>
/// parameter that points every REST 401 at it (#127).
///
/// <para><b>Why a REST document of its own, rather than the root one.</b> The document at
/// <c>/.well-known/oauth-protected-resource</c> is served by the MCP SDK and describes the MCP
/// resource: its <c>resource</c> is <c>Auth0:McpIdentifier</c> and it lists the
/// <c>read:printdata</c>/<c>write:printdata</c> scopes. REST tokens are a different audience
/// (<c>Auth0:ApiIdentifier</c>) and the default bearer scheme rejects an MCP-audience token, so
/// pointing a REST 401 there would send an agent off to fetch a token the REST API then refuses.
/// This document names the REST audience and no scopes, because REST checks audience only (see
/// the OpenAPI notes in AGENTS.md). Leave the root document alone: MCP clients fall back to it.</para>
///
/// <para><b>Why a response hook, not <c>JwtBearerEvents.OnChallenge</c>.</b> A REST 401 comes
/// from three places: the bearer challenge, <c>ApiKeyMiddleware</c> rejecting a key (which
/// writes its own body and never challenges), and whichever scheme a host substitutes (the dev
/// bypass, the integration test handler). OnChallenge sees only the first, and adding a
/// parameter there means taking over the whole header the handler builds from its error state.
/// Appending at <c>OnStarting</c> covers all three and leaves the framework's own
/// <c>error</c>/<c>error_description</c> parameters exactly as they were.</para>
///
/// <para><c>/mcp</c> is excluded. Its 401 header is what MCP OAuth discovery reads, and it
/// already carries its own <c>resource_metadata</c>.</para>
/// </summary>
public static class RestProtectedResource
{
    /// <summary>
    /// RFC 9728 path insertion for the REST API's base path, <c>/api</c>. It deliberately sits
    /// beside the MCP SDK's <c>/mcp</c> document rather than replacing the root one.
    /// </summary>
    public const string MetadataPath = "/.well-known/oauth-protected-resource/api";

    private static readonly PathString McpPath = new("/mcp");

    /// <summary>
    /// The public base URL this API is reached at: <c>OpenApi:ServerUrl</c> when set (production,
    /// where the App Service front end makes the request's own scheme unreliable), otherwise
    /// the request's origin, matching the OpenAPI document's <c>servers</c> entry.
    /// </summary>
    public static string PublicBaseUrl(HttpRequest request, IConfiguration configuration)
    {
        var configured = configuration["OpenApi:ServerUrl"];
        return string.IsNullOrWhiteSpace(configured)
            ? $"{request.Scheme}://{request.Host}{request.PathBase}"
            : configured.TrimEnd('/');
    }

    /// <summary>
    /// Adds <c>resource_metadata</c> to the <c>WWW-Authenticate</c> header of every 401 outside
    /// <c>/mcp</c>. Register before authentication so the hook is in place for every 401 source.
    /// </summary>
    public static IApplicationBuilder UseRestResourceMetadataChallenge(this IApplicationBuilder app)
    {
        var configuration = app.ApplicationServices.GetRequiredService<IConfiguration>();

        return app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments(McpPath))
            {
                context.Response.OnStarting(() =>
                {
                    if (context.Response.StatusCode == StatusCodes.Status401Unauthorized)
                    {
                        var metadataUrl = PublicBaseUrl(context.Request, configuration) + MetadataPath;
                        context.Response.Headers.WWWAuthenticate =
                            AddResourceMetadata(context.Response.Headers.WWWAuthenticate, metadataUrl);
                    }
                    return Task.CompletedTask;
                });
            }

            await next(context);
        });
    }

    /// <summary>
    /// Appends the parameter to each Bearer challenge that lacks one, or adds a bare Bearer
    /// challenge when there is none at all (the API-key rejection). REST accepts a bearer token
    /// on every endpoint an API key reaches, so that challenge is accurate, not decorative.
    /// </summary>
    internal static string[] AddResourceMetadata(IEnumerable<string?> challenges, string metadataUrl)
    {
        var parameter = $"resource_metadata=\"{metadataUrl}\"";
        var result = new List<string>();
        var sawBearer = false;

        foreach (var challenge in challenges)
        {
            if (string.IsNullOrWhiteSpace(challenge))
            {
                continue;
            }

            var isBearer = challenge.Equals(JwtBearerScheme, StringComparison.OrdinalIgnoreCase)
                || challenge.StartsWith(JwtBearerScheme + " ", StringComparison.OrdinalIgnoreCase);

            if (!isBearer)
            {
                result.Add(challenge);
                continue;
            }

            sawBearer = true;
            if (challenge.Contains("resource_metadata=", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(challenge);
            }
            else if (challenge.Length == JwtBearerScheme.Length)
            {
                result.Add($"{JwtBearerScheme} {parameter}");
            }
            else
            {
                result.Add($"{challenge}, {parameter}");
            }
        }

        if (!sawBearer)
        {
            result.Insert(0, $"{JwtBearerScheme} {parameter}");
        }

        return [.. result];
    }

    private const string JwtBearerScheme = "Bearer";

    /// <summary>
    /// Serves the REST metadata document. Anonymous by definition.
    ///
    /// <para>Middleware, not a mapped endpoint, and it must run before <c>UseAuthentication</c>.
    /// The MCP SDK's metadata handler is an authentication request handler, so it runs inside
    /// <c>UseAuthentication</c> and answers every path under
    /// <c>/.well-known/oauth-protected-resource</c> with the MCP document before any endpoint is
    /// reached. A <c>MapGet</c> here was tried first and silently served the MCP resource.</para>
    /// </summary>
    public static IApplicationBuilder UseRestProtectedResourceMetadata(this IApplicationBuilder app)
    {
        var configuration = app.ApplicationServices.GetRequiredService<IConfiguration>();
        var metadata = new Metadata(
            configuration["Auth0:ApiIdentifier"],
            [$"https://{configuration["Auth0:Domain"]}/"],
            ["header"],
            ProblemDetailsStatusCodePages.DocumentationUrl);

        return app.MapWhen(
            context => (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                && context.Request.Path.Equals(MetadataPath, StringComparison.OrdinalIgnoreCase),
            branch => branch.Run(context => context.Response.WriteAsJsonAsync(
                metadata, MetadataJsonOptions, "application/json", context.RequestAborted)));
    }

    private sealed record Metadata(
        [property: JsonPropertyName("resource")] string? Resource,
        [property: JsonPropertyName("authorization_servers")] string[] AuthorizationServers,
        [property: JsonPropertyName("bearer_methods_supported")] string[] BearerMethodsSupported,
        [property: JsonPropertyName("resource_documentation")] string ResourceDocumentation);

    // Plain options: the property names are pinned by attribute, and the document must not
    // pick up whatever naming policy the MVC or minimal-API options happen to carry.
    private static readonly System.Text.Json.JsonSerializerOptions MetadataJsonOptions = new();
}
