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

    /// <summary>
    /// 429: too many unknown device tokens from this address. Not <see cref="InvalidToken"/>, so a
    /// bridge behind a busy NAT backs off instead of concluding it must re-pair.
    /// </summary>
    public const string Throttled = "device_token_throttled";

    /// <summary>
    /// 403 at bridge check-in and the desired list: the token is one a re-pair replaced, still in
    /// its grace (api#287). It logs prints and heartbeats until the grace ends, nothing more.
    /// </summary>
    public const string TokenSuperseded = "device_token_superseded";

    /// <summary>
    /// 403 at pairing: the Auth0 token carries <c>offline_access</c>, so it came with (or from) a
    /// refresh token that could pair again after a revoke. Run the device flow without it.
    /// </summary>
    public const string OfflineAccessNotAllowed = "offline_access_not_allowed";

    /// <summary>403 at pairing: this Auth0 token (or its <c>jti</c>) was already exchanged. Run the device flow again.</summary>
    public const string PairingTokenUsed = "pairing_token_used";

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
    /// 409: the user revoked this device while it re-paired. The pairing token is spent, so the
    /// device runs the device flow again and the user approves it anew.
    /// </summary>
    public const string GrantRevoked = "device_grant_revoked";

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
