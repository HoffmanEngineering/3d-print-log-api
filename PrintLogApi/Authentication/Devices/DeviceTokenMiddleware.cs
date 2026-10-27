using System.Globalization;
using System.Security.Claims;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Caching.Memory;
using PrintLogApi.Caching;
using PrintLogApi.Services.DeviceGrants;

namespace PrintLogApi.Authentication.Devices;

/// <summary>
/// Authenticates a device token (#148) on <c>/api</c>: <c>Authorization: Bearer pld_...</c>.
/// Runs after <c>UseAuthentication</c> and before <c>ApiKeyMiddleware</c> and authorization, the
/// same place and the same way API keys are handled, so the endpoints a device may call need no
/// policy of their own.
///
/// The scope check is here, not in a policy, because a policy is only evaluated on endpoints that
/// ask for one: an anonymous endpoint (a public print, a sitemap) never runs authorization, and it
/// would serve a device-authenticated owner their private data. So the principal is only ever set
/// on an endpoint carrying <see cref="DeviceAccessAttribute"/> whose scope the grant holds; on
/// every other endpoint the request ends here with 403, before binding or the handler.
/// </summary>
public sealed class DeviceTokenMiddleware(RequestDelegate next, IMemoryCache cache, IConfiguration configuration)
{
    public const string AuthenticationType = "DeviceToken";

    /// <summary>The role a device principal is in. <c>InteractiveUserOnly</c> refuses it, like <c>ApiUser</c>.</summary>
    public const string Role = "DeviceUser";

    private static readonly PathString ApiPath = new("/api");
    private const string FailedAttemptCachePrefix = "devicetoken_failed:";

    private readonly int _maxFailedAttemptsPerMinute = configuration.GetValue("Api:InvalidDeviceTokenAttemptsPerMinute", 20);

    public async Task InvokeAsync(HttpContext context, IDeviceGrantService grants, TelemetryClient telemetry)
    {
        var token = context.Request.Path.StartsWithSegments(ApiPath)
            ? DeviceTokenFormat.FromAuthorizationHeader(context.Request.Headers.Authorization.ToString())
            : null;

        if (token is null)
        {
            await next(context);
            return;
        }

        if (context.Request.Headers.ContainsKey("X-Api-Key") || context.Request.Query.ContainsKey("api_key"))
        {
            TrackRejected(telemetry, "multiple_credentials", null);
            await DeviceTokenErrors.WriteAsync(context, StatusCodes.Status400BadRequest, DeviceTokenErrors.MultipleCredentials,
                "Send a device token or an API key, not both.");
            return;
        }

        var identity = await grants.AuthenticateAsync(token, context.RequestAborted);
        if (identity is null)
        {
            if (RegisterFailedAttempt(context))
            {
                TrackRejected(telemetry, "throttled", null);
                context.Response.Headers.RetryAfter = "60";
                await DeviceTokenErrors.WriteAsync(context, StatusCodes.Status429TooManyRequests, DeviceTokenErrors.Throttled,
                    "Too many invalid device token attempts.");
                return;
            }

            TrackRejected(telemetry, "invalid_token", null);
            await DeviceTokenErrors.WriteAsync(context, StatusCodes.Status401Unauthorized, DeviceTokenErrors.InvalidToken,
                "The device token is not valid. It may have been revoked or replaced; pair the device again.",
                "Bearer error=\"invalid_token\"");
            return;
        }

        // No endpoint means no route matched: let routing answer 404 for an anonymous request.
        var endpoint = context.GetEndpoint();
        if (endpoint is not null)
        {
            var access = endpoint.Metadata.GetMetadata<DeviceAccessAttribute>();
            if (access is null)
            {
                TrackRejected(telemetry, "endpoint_not_allowed", identity.GrantId);
                await DeviceTokenErrors.WriteAsync(context, StatusCodes.Status403Forbidden, DeviceTokenErrors.EndpointNotAllowed,
                    "Device tokens cannot be used on this endpoint.");
                return;
            }

            if (access.Scope is not null && !identity.Scopes.Contains(access.Scope))
            {
                TrackRejected(telemetry, "insufficient_scope", identity.GrantId);
                await DeviceTokenErrors.WriteAsync(context, StatusCodes.Status403Forbidden, DeviceTokenErrors.InsufficientScope,
                    $"This device was not granted the {access.Scope} scope.",
                    $"Bearer error=\"insufficient_scope\", scope=\"{access.Scope}\"");
                return;
            }

            context.User = CreatePrincipal(identity);
            await grants.TouchLastUsedAsync(identity.GrantId, context.RequestAborted);
        }

        await next(context);
    }

    /// <summary>
    /// One event per refused device-token request, by reason, with the grant when the token was
    /// valid. Never the token or its hash. A flood of guesses shows up as <c>throttled</c>; the SDK's
    /// default adaptive sampling keeps it from costing more than any other request flood.
    /// </summary>
    private static void TrackRejected(TelemetryClient telemetry, string reason, long? grantId)
    {
        var properties = new Dictionary<string, string> { ["Reason"] = reason };
        if (grantId is { } id)
        {
            properties["GrantId"] = id.ToString(CultureInfo.InvariantCulture);
        }

        telemetry.TrackEvent("DeviceTokenRejected", properties);
    }

    private static ClaimsPrincipal CreatePrincipal(DeviceGrantIdentity grant)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, grant.UserId.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Role, Role),
            new(AuthMethodClaim.Type, AuthMethodClaim.Device),
            new(DeviceGrantClaim.Type, grant.GrantId.ToString(CultureInfo.InvariantCulture)),
            new(ConnectorCredentialClaim.Type, ConnectorCredentialClaim.ForDeviceGrant(grant.GrantId)),
        };

        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationType, ClaimTypes.NameIdentifier, ClaimTypes.Role));
    }

    /// <summary>
    /// Counts an unknown token against the calling address, as <c>ApiKeyMiddleware</c> does for
    /// keys and for the same reason: this short-circuits before <c>UseRateLimiter</c>, so nothing
    /// else bounds guessing. Only failures count; a valid token never accumulates. Guessing a
    /// 256-bit token is hopeless anyway, so this mainly keeps a misconfigured client from turning
    /// into a query per request. <c>Api:InvalidDeviceTokenAttemptsPerMinute</c>, 0 disables.
    /// </summary>
    private bool RegisterFailedAttempt(HttpContext context)
    {
        if (_maxFailedAttemptsPerMinute <= 0)
        {
            return false;
        }

        var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var counter = cache.GetOrCreate(FailedAttemptCachePrefix + address, entry =>
        {
            entry.SetSize(CacheBudget.SmallEntryBytes);
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            return new FailedAttemptCounter();
        })!;

        return Interlocked.Increment(ref counter.Count) > _maxFailedAttemptsPerMinute;
    }

    private sealed class FailedAttemptCounter
    {
        public int Count;
    }
}
