using System.Text.Json;

namespace PrintLogApi.Authentication.Spoolman;

/// <summary>
/// Names and pipeline pieces for the Spoolman-compatible API's own authentication (#176).
/// </summary>
public static class SpoolmanAuthentication
{
    /// <summary>The scheme: HTTP Basic, with a Spoolman connection secret as the password.</summary>
    public const string Scheme = "SpoolmanBasic";

    /// <summary>
    /// The policy every <c>/spoolman</c> endpoint requires. It pins <see cref="Scheme"/>, so no
    /// JWT, API key or dev-bypass identity is ever evaluated there, and requires the connection
    /// claim only this scheme issues.
    /// </summary>
    public const string Policy = "Spoolman";

    public const string PrinterIdClaim = "spoolman_printer_id";
    public const string ConnectionIdClaim = "spoolman_connection_id";

    /// <summary>
    /// <c>HttpContext.Items</c> key for the authenticated secret's hash, which an open socket
    /// re-resolves on its timer (#178). An item, not a claim: claims are what anything that logs a
    /// principal would log, and the hash is the lookup value.
    /// </summary>
    public const string SecretHashItem = "spoolman_secret_hash";

    /// <summary>Where the Spoolman routes live; Moonraker appends <c>/api/v1/...</c> to its <c>server</c>.</summary>
    public static readonly PathString PathPrefix = new("/spoolman");

    public static bool IsSpoolmanPath(HttpContext context) => context.Request.Path.StartsWithSegments(PathPrefix);

    /// <summary>
    /// Whether this request is a websocket handshake. Read from the headers rather than only from
    /// <c>HttpContext.WebSockets</c>, whose feature exists only once <c>UseWebSockets</c> has run.
    /// </summary>
    public static bool IsHandshake(HttpContext context) =>
        context.WebSockets.IsWebSocketRequest
        || (HttpMethods.IsGet(context.Request.Method)
            && string.Equals(context.Request.Headers.Upgrade.ToString(), "websocket", StringComparison.OrdinalIgnoreCase));

    /// <summary>Writes Spoolman's error shape, <c>{"message": "..."}</c>, as <c>application/json</c>.</summary>
    public static Task WriteErrorAsync(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new { message }));
    }

    /// <summary>
    /// Gives every body-less error under <c>/spoolman</c> Spoolman's <c>{"message"}</c> body, the
    /// counterpart of <c>ProblemDetailsStatusCodePages</c> for the REST API, which excludes this
    /// path. Spoolman clients expect this shape, and several print <c>message</c> as is.
    /// </summary>
    public static IApplicationBuilder UseSpoolmanErrorBodies(this IApplicationBuilder app) =>
        app.UseWhen(IsSpoolmanPath, branch => branch.UseStatusCodePages(statusContext =>
        {
            var status = statusContext.HttpContext.Response.StatusCode;
            var message = status switch
            {
                StatusCodes.Status401Unauthorized => "Authentication required.",
                StatusCodes.Status403Forbidden => "Not allowed.",
                StatusCodes.Status404NotFound => "Not found.",
                StatusCodes.Status405MethodNotAllowed => "Method not allowed.",
                StatusCodes.Status429TooManyRequests => "Too many requests.",
                >= 500 => "Internal server error.",
                _ => "Request failed.",
            };
            return WriteErrorAsync(statusContext.HttpContext, status, message);
        }));
}
