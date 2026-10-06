using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using PrintLogApi.Authentication;

namespace PrintLogApi.Middleware;

/// <summary>
/// Gives an RFC 7807 body to every 4xx/5xx response that would otherwise have none (#127):
/// an unknown path (404), a wrong method (405), a challenge (401), a forbid (403), a rate-limit
/// rejection (429). Each used to come back with an empty body and no content type, which leaves
/// an agent that took a wrong turn nothing to reason about.
///
/// <para><b>Additive only.</b> The status-code-pages middleware writes only when the response
/// has not started and carries no body and no content type, so every body the API already
/// produces — MVC's validation problems, <c>ApiKeyMiddleware</c>'s text, the readiness JSON —
/// passes through untouched. Changing those is #73's job, because the slicer plugins parse
/// them. Nothing can be parsing a body that did not exist.</para>
///
/// <para><b>Members are fixed</b> at <c>type</c>, <c>title</c>, <c>status</c>, <c>detail</c> and
/// <c>traceId</c>. The detail is a resolution hint chosen by status code alone, never anything
/// derived from the request beyond its method, so no existence oracle or internal state leaks
/// through it.</para>
///
/// <para><b>Written whatever the Accept header says.</b> Content negotiation would answer an
/// agent that sent <c>Accept: text/html</c> with an empty body again, which is the thing being
/// fixed.</para>
///
/// <para><b><c>/mcp</c> is excluded.</b> It has its own error contract, and its 401 must stay
/// exactly as it is.</para>
/// </summary>
public static class ProblemDetailsStatusCodePages
{
    /// <summary>The human- and agent-readable REST API documentation.</summary>
    public const string DocumentationUrl = "https://www.3dprintlog.com/docs/api";

    private const string OpenApiDocumentPath = "/swagger/v1/swagger.json";

    private static readonly PathString McpPath = new("/mcp");

    public static IApplicationBuilder UseProblemDetailsStatusCodePages(this IApplicationBuilder app)
    {
        var configuration = app.ApplicationServices.GetRequiredService<IConfiguration>();

        return app.UseWhen(
            context => !context.Request.Path.StartsWithSegments(McpPath),
            branch => branch.UseStatusCodePages(statusContext =>
            {
                var httpContext = statusContext.HttpContext;
                var status = httpContext.Response.StatusCode;

                var problem = new ProblemDetails
                {
                    Status = status,
                    Detail = DetailFor(httpContext, status, configuration),
                };
                problem.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                // ProblemHttpResult fills in type and title from the status code, and writes
                // application/problem+json without consulting the Accept header.
                return TypedResults.Problem(problem).ExecuteAsync(httpContext);
            }));
    }

    private static string? DetailFor(HttpContext context, int status, IConfiguration configuration)
    {
        var baseUrl = RestProtectedResource.PublicBaseUrl(context.Request, configuration);
        var references = $"See {DocumentationUrl} and {baseUrl}{OpenApiDocumentPath}.";

        return status switch
        {
            StatusCodes.Status401Unauthorized =>
                "This endpoint requires authentication. Send an OAuth 2.0 access token as "
                + "'Authorization: Bearer <token>', or an API key in the X-Api-Key header. The "
                + "resource_metadata parameter of the WWW-Authenticate header points to the "
                + $"protected-resource metadata that names the authorization server. {references}",
            StatusCodes.Status403Forbidden =>
                $"The credentials were accepted but do not grant access to this resource. {references}",
            // Every controller is an [ApiController], whose NotFound() already carries a body,
            // so a body-less 404 is nearly always routing finding nothing. The endpoint check
            // keeps the wording true for the rare one that matched and still came back empty.
            StatusCodes.Status404NotFound when context.GetEndpoint() is null =>
                $"No endpoint exists at this path. {references}",
            StatusCodes.Status404NotFound =>
                $"The requested resource was not found. {references}",
            StatusCodes.Status405MethodNotAllowed =>
                $"This path does not accept the {context.Request.Method} method. The Allow header "
                + $"lists the methods it does accept. {references}",
            StatusCodes.Status429TooManyRequests =>
                "Too many requests. Wait for the number of seconds in the Retry-After header, "
                + "when present, before retrying.",
            _ => null,
        };
    }
}
