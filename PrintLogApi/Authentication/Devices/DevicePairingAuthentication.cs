using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace PrintLogApi.Authentication.Devices;

/// <summary>
/// The pairing half of device tokens (#148). A device runs Auth0's device authorization flow
/// against the Native application in <see cref="DevicePairingOptions.ClientId"/>, for the
/// <see cref="DevicePairingOptions.Audience"/> API, and exchanges the resulting access token once
/// at <c>POST /api/device-grants</c>. That token is accepted only here: the device audience is
/// neither the web app's nor the MCP one, so the default and MCP bearer schemes reject it, and
/// this scheme rejects theirs.
/// </summary>
public static class DevicePairingAuthentication
{
    public const string Scheme = "DevicePairing";
    public const string Policy = "DevicePairing";

    public static IServiceCollection AddDevicePairing(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DevicePairingOptions>()
            .Bind(configuration.GetSection(DevicePairingOptions.Section))
            .PostConfigure(o =>
            {
                if (string.IsNullOrWhiteSpace(o.Domain))
                {
                    o.Domain = configuration["Auth0:Domain"];
                }
            });

        // Registered in every environment, including the dev bypass, which never reaches it: the
        // policy below pins this scheme. Unconfigured, it has no authority, no keys and no
        // audience, so it validates nothing and makes no network call.
        services.AddAuthentication().AddJwtBearer(Scheme, _ => { });
        services.AddOptions<JwtBearerOptions>(Scheme)
            .Configure<IOptions<DevicePairingOptions>>((jwt, pairing) =>
            {
                var options = pairing.Value;
                if (!options.IsConfigured)
                {
                    return;
                }

                jwt.Authority = options.Issuer;
                jwt.Audience = options.Audience;
                jwt.TokenValidationParameters.ValidIssuer = options.Issuer;
            });

        services.AddAuthorization(o => o.AddPolicy(Policy, policy =>
        {
            policy.AuthenticationSchemes.Add(Scheme);
            policy.RequireAuthenticatedUser();
            policy.Requirements.Add(new DevicePairingRequirement());
        }));

        services.AddSingleton<IAuthorizationHandler, DevicePairingHandler>();
        return services;
    }

    /// <summary>
    /// Whether a pairing token's <c>scope</c> includes <c>offline_access</c>. Such a token came with
    /// a refresh token, or from one (Auth0 keeps the scope on refreshed access tokens), and that
    /// refresh token could mint fresh pairing tokens after the user revokes the device. Revoking
    /// in this API cannot reach it, so those tokens may not pair at all.
    /// </summary>
    public static bool CarriesOfflineAccess(string? scope) =>
        (scope ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("offline_access", StringComparer.Ordinal);

    /// <summary>
    /// What spending this pairing token records: a hash of its <c>jti</c> when it has one (Auth0's
    /// identifier, unique per token), else of the whole token, which is just as unique per
    /// issuance. Remembered until the token's own <c>exp</c> (plus the bearer handler's default
    /// five-minute clock skew), after which it cannot authenticate whatever the configuration
    /// says; a token without <c>exp</c> (the handler requires one) falls back to its age limit.
    /// </summary>
    public static Services.DeviceGrants.PairingTokenSpend SpendOf(ClaimsPrincipal user, string authorizationHeader, DevicePairingOptions options)
    {
        var jti = user.FindFirst("jti")?.Value;
        var key = string.IsNullOrEmpty(jti)
            ? "token:" + DeviceTokenFormat.BearerValue(authorizationHeader)
            : "jti:" + jti;

        DateTime expiresAt;
        if (long.TryParse(user.FindFirst("exp")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var exp))
        {
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime.AddMinutes(5);
        }
        else
        {
            var iat = long.TryParse(user.FindFirst("iat")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var issued) ? issued : 0;
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(iat).UtcDateTime.AddMinutes(options.MaxPairingTokenAgeMinutes + 1);
        }

        return new Services.DeviceGrants.PairingTokenSpend(DeviceTokenFormat.Hash(key), expiresAt);
    }
}

public sealed class DevicePairingRequirement : IAuthorizationRequirement;

/// <summary>
/// What a valid device-audience JWT must also show before it may mint a grant: pairing is
/// configured, the token came from the pairing app (<c>azp</c>), it is fresh, and it maps to a
/// local user. Audience and issuer are the scheme's checks; scopes are the action's, so it can
/// answer with a code.
/// </summary>
public sealed class DevicePairingHandler(IOptionsMonitor<DevicePairingOptions> options, TimeProvider clock)
    : AuthorizationHandler<DevicePairingRequirement>
{
    private static readonly TimeSpan AllowedClockSkew = TimeSpan.FromMinutes(1);

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, DevicePairingRequirement requirement)
    {
        var settings = options.CurrentValue;
        var user = context.User;

        if (!settings.IsConfigured
            || !string.Equals(user.FindFirst("azp")?.Value, settings.ClientId, StringComparison.Ordinal)
            || user.FindFirst(ClaimTypes.NameIdentifier) is null
            || !IsFresh(user.FindFirst("iat")?.Value, settings.MaxPairingTokenAgeMinutes))
        {
            return Task.CompletedTask;
        }

        context.Succeed(requirement);
        return Task.CompletedTask;
    }

    private bool IsFresh(string? iat, int maxAgeMinutes)
    {
        if (!long.TryParse(iat, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        var issued = DateTimeOffset.FromUnixTimeSeconds(seconds);
        var now = clock.GetUtcNow();
        return issued <= now + AllowedClockSkew && now - issued <= TimeSpan.FromMinutes(maxAgeMinutes);
    }
}
