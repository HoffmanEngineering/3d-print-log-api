using Microsoft.AspNetCore.Mvc;

namespace PrintLogApi.Authentication.Devices;

/// <summary>
/// The machine-readable <c>code</c>s device pairing and device tokens answer with (#148). They are
/// a contract with printlog-bridge: <see cref="InvalidToken"/> is how it knows to ask for a re-pair.
/// </summary>
public static class DeviceTokenErrors
{
    /// <summary>401: no grant has this token (never issued, rotated by a re-pair, or revoked).</summary>
    public const string InvalidToken = "invalid_device_token";

    /// <summary>403: a device token is not accepted on this endpoint at all.</summary>
    public const string EndpointNotAllowed = "device_endpoint_not_allowed";

    /// <summary>403: the endpoint takes device tokens, but this grant lacks its scope.</summary>
    public const string InsufficientScope = "insufficient_scope";

    /// <summary>400: a device token and an API key on one request.</summary>
    public const string MultipleCredentials = "multiple_credentials";

    /// <summary>503: pairing is not configured on this deployment.</summary>
    public const string PairingUnavailable = "device_pairing_unavailable";

    /// <summary>409: the user has paired as many devices as allowed.</summary>
    public const string GrantLimit = "device_grant_limit";

    /// <summary>
    /// Writes a problem details body with a <c>code</c>, from middleware, where there is no
    /// controller to do it. The body is ours, so <c>ProblemDetailsStatusCodePages</c> leaves it.
    /// </summary>
    public static Task WriteAsync(HttpContext context, int status, string code, string detail, string? wwwAuthenticate = null)
    {
        context.Response.StatusCode = status;
        if (wwwAuthenticate is not null)
        {
            context.Response.Headers.WWWAuthenticate = wwwAuthenticate;
        }

        var problem = new ProblemDetails { Status = status, Detail = detail, Title = ReasonPhrase(status) };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = context.TraceIdentifier;
        return Results.Problem(problem).ExecuteAsync(context);
    }

    private static string ReasonPhrase(int status) => Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status);
}
