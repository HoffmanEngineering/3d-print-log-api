using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PrintLogApi.Caching;
using PrintLogApi.Services.Spoolman;

namespace PrintLogApi.Authentication.Spoolman;

/// <summary>
/// Authenticates the Spoolman-compatible API with a per-printer connection secret sent as HTTP
/// Basic (#176).
///
/// <para><b>Why Basic.</b> Moonraker's <c>[spoolman]</c> component can send no custom header, but
/// it keeps the userinfo of its <c>server</c> URL, and Tornado's simple HTTP client turns that into
/// <c>Authorization: Basic</c> on both REST requests and the websocket handshake. The password is
/// the secret; the username is cosmetic (<c>printer-12</c> in the block the API hands out). An
/// empty password with a non-empty username is read the other way round, for clients that can
/// only set one field. Carrying the secret in a header rather than the path keeps it out of every
/// request URL App Service, App Insights and the metrics ever see.</para>
///
/// <para><b>What it costs.</b> One cached lookup (<see cref="ISpoolmanCredentialCache"/>, revoked
/// and paused results included, unknown secrets never cached) and, at most once a minute per
/// connection, a <c>LastSeenAt</c> update. The handshake does more: it re-applies the user's tier, so a lapsed subscription the billing path
/// missed still pauses extra printers, and records the network for the fair-use count. Moonraker
/// sends no usage while its socket is down, so refusing at the handshake also stops a refused
/// printer's REST traffic. Pro is re-checked for open sockets on a timer (#178), not per request.</para>
///
/// <para><b>Failed attempts.</b> Only a secret that matches nothing counts, per client address,
/// like <c>ApiKeyMiddleware</c>, and only failing attempts are ever answered with the 429 (with a
/// rounded-up <c>Retry-After</c>). A revoked or paused printer's retries are known credentials
/// answered from the cache and are not counted; counting them, or refusing a valid secret from
/// an address over the limit, would let one printer lock out a working one on the same network.
/// The counter is in-process, which is fine on one App Service instance.</para>
///
/// <para><b>Never logged.</b> Nothing here logs the header, the secret or its hash. The connector
/// claim is a domain-separated hash of the secret (<see cref="ConnectorCredentialClaim.ForSpoolman"/>),
/// so the rate limiter's partition keys hold neither the secret nor the stored lookup value.</para>
/// </summary>
public sealed class SpoolmanBasicAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ISpoolmanCredentialCache credentials,
    IMemoryCache memoryCache,
    IConfiguration configuration,
    TimeProvider clock) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string InvalidAttemptsPerMinuteKey = "Spoolman:InvalidAttemptsPerMinute";
    public const int DefaultInvalidAttemptsPerMinute = 20;

    private const string FailedAttemptPrefix = "spoolman_failed:";
    private const string LastSeenPrefix = "spoolman_lastseen:";
    private const string NetworkPrefix = "spoolman_network:";
    private const string FailureItem = "spoolman_auth_failure";
    private const string RetryAfterItem = "spoolman_auth_retry_after";

    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LastSeenThrottle = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan NetworkThrottle = TimeSpan.FromHours(1);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization.ToString(), out var header)
            || !string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        if (ReadSecret(header.Parameter) is not { } secret)
        {
            return FailAttempt("The Spoolman credential is not valid Basic authentication.");
        }

        var hash = SpoolmanSecret.Hash(secret);
        var credential = await credentials.ResolveAsync(hash, Context.RequestAborted);

        switch (credential.Status)
        {
            case SpoolmanCredentialStatus.Unknown:
                return FailAttempt("This Spoolman connection is not recognised. Check the token in moonraker.conf, or reconnect the printer in 3D Print Log.");
            case SpoolmanCredentialStatus.Revoked:
                return Fail("This Spoolman connection was revoked. Reconnect the printer in 3D Print Log for a new token.");
            case SpoolmanCredentialStatus.Disabled:
                return Fail(PausedMessage);
        }

        if (SpoolmanAuthentication.IsHandshake(Context))
        {
            credential = await CheckHandshakeAsync(credential, hash);
            if (credential.Status != SpoolmanCredentialStatus.Valid)
            {
                return Fail(PausedMessage);
            }
        }

        await StampLastSeenAsync(credential.ConnectionId);
        Context.Items[SpoolmanAuthentication.SecretHashItem] = hash;

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, credential.UserId.ToString(CultureInfo.InvariantCulture)),
            new(SpoolmanAuthentication.ConnectionIdClaim, credential.ConnectionId.ToString()),
            new(ConnectorCredentialClaim.Type, ConnectorCredentialClaim.ForSpoolman(secret)),
        };
        if (credential.PrinterId is { } printerId)
        {
            claims.Add(new Claim(SpoolmanAuthentication.PrinterIdClaim, printerId.ToString(CultureInfo.InvariantCulture)));
        }

        var identity = new ClaimsIdentity(claims, SpoolmanAuthentication.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SpoolmanAuthentication.Scheme));
    }

    private const string PausedMessage =
        "This Spoolman connection is paused: the Free plan includes 1 connected printer. Upgrade to Pro, or disconnect your other printer in 3D Print Log.";

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Context.Items[RetryAfterItem] is int retryAfter)
        {
            Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
            return SpoolmanAuthentication.WriteErrorAsync(Context, StatusCodes.Status429TooManyRequests,
                "Too many failed attempts. Try again later.");
        }

        Response.Headers.WWWAuthenticate = "Basic realm=\"3D Print Log Spoolman\", charset=\"UTF-8\"";
        return SpoolmanAuthentication.WriteErrorAsync(Context, StatusCodes.Status401Unauthorized,
            Context.Items[FailureItem] as string ?? "Authentication required. Put this printer's Spoolman token in moonraker.conf.");
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        SpoolmanAuthentication.WriteErrorAsync(Context, StatusCodes.Status403Forbidden, "Not allowed.");

    /// <summary>
    /// Fails an attempt with a secret that matches nothing, counting it against the caller's
    /// address. Checked after the lookup, never before, as in <c>ApiKeyMiddleware</c>: a valid
    /// secret is never refused because of failures from the same address, which matters when
    /// a front end collapses every caller into one address and one printer's typo would
    /// otherwise lock out all the others.
    /// </summary>
    private AuthenticateResult FailAttempt(string message)
    {
        if (RegisterFailure() is { } retryAfter)
        {
            Context.Items[RetryAfterItem] = retryAfter;
            return Fail("Too many failed attempts. Try again later.");
        }

        return Fail(message);
    }

    private AuthenticateResult Fail(string message)
    {
        Context.Items[FailureItem] = message;
        return AuthenticateResult.Fail(message);
    }

    /// <summary>The secret from a Basic parameter: the password, or the username when the password is empty.</summary>
    private static string? ReadSecret(string? parameter)
    {
        if (string.IsNullOrEmpty(parameter))
        {
            return null;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parameter));
        }
        catch (FormatException)
        {
            return null;
        }

        var colon = decoded.IndexOf(':');
        var secret = colon < 0
            ? decoded
            : decoded[(colon + 1)..] is { Length: > 0 } password ? password : decoded[..colon];

        // Moonraker sends userinfo as it appears in moonraker.conf, which a user may have
        // percent-encoded. A real secret has no '%', so decoding cannot change a valid one.
        secret = Uri.UnescapeDataString(secret).Trim();
        return secret.Length == 0 ? null : secret;
    }

    /// <summary>
    /// The handshake-only work: re-apply the tier (pausing this connection if a lapsed Free user
    /// has more than one) and record the network. Returns the credential as it now stands.
    /// </summary>
    private async Task<SpoolmanCredential> CheckHandshakeAsync(SpoolmanCredential credential, string hash)
    {
        var connections = Context.RequestServices.GetRequiredService<ISpoolmanConnectionService>();

        var changed = await connections.ApplyTierAsync(credential.UserId, Context.RequestAborted);
        if (changed.Contains(credential.ConnectionId))
        {
            await credentials.InvalidateAsync([hash]);
            credential = await credentials.ResolveAsync(hash, Context.RequestAborted);
        }

        if (credential.Status == SpoolmanCredentialStatus.Valid && Context.Connection.RemoteIpAddress is { } address)
        {
            var key = NetworkPrefix + credential.ConnectionId + ":" + SpoolmanNetwork.Prefix(address);
            // Throttled only once recorded, so a failed record is retried on the next handshake.
            if (!memoryCache.TryGetValue(key, out _)
                && await connections.RecordNetworkAsync(credential.UserId, address, Context.RequestAborted))
            {
                memoryCache.Set(key, true, new MemoryCacheEntryOptions()
                    .SetSize(CacheBudget.SmallEntryBytes)
                    .SetAbsoluteExpiration(NetworkThrottle));
            }
        }

        return credential;
    }

    /// <summary>
    /// <c>LastSeenAt</c>, at most once a minute per connection. A throttle flag on IMemoryCache,
    /// like <c>UserApiKeyService</c>'s last-used flag: its existence is the whole signal.
    /// </summary>
    private async Task StampLastSeenAsync(Guid connectionId)
    {
        var key = LastSeenPrefix + connectionId;
        if (memoryCache.TryGetValue(key, out _))
        {
            return;
        }

        memoryCache.Set(key, true, new MemoryCacheEntryOptions()
            .SetSize(CacheBudget.SmallEntryBytes)
            .SetAbsoluteExpiration(LastSeenThrottle));

        var now = clock.GetUtcNow().UtcDateTime;
        await Context.RequestServices.GetRequiredService<PrintLogContext>().SpoolmanConnections
            .Where(c => c.Id == connectionId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastSeenAt, now), Context.RequestAborted);
    }

    private int MaxFailures => configuration.GetValue(InvalidAttemptsPerMinuteKey, DefaultInvalidAttemptsPerMinute);

    private string FailureKey => FailedAttemptPrefix + (Context.Connection.RemoteIpAddress?.ToString() ?? "unknown");

    /// <summary>
    /// Records one failed attempt for this address. Returns the seconds until its window ends when
    /// that puts it over the limit, or null while it is within it.
    /// </summary>
    private int? RegisterFailure()
    {
        var max = MaxFailures;
        if (max <= 0)
        {
            return null;
        }

        var expires = clock.GetUtcNow() + FailureWindow;
        // A mutable box incremented in place, as in ApiKeyMiddleware: a read-modify-write through
        // the cache would race under concurrent guesses.
        var counter = memoryCache.GetOrCreate(FailureKey, entry =>
        {
            entry.SetSize(CacheBudget.SmallEntryBytes);
            entry.AbsoluteExpiration = expires;
            return new FailureWindowCounter { Expires = expires };
        })!;

        if (Interlocked.Increment(ref counter.Count) <= max)
        {
            return null;
        }

        // Rounded up, never down, for the reason given in Startup's OnRejected.
        var remaining = counter.Expires - clock.GetUtcNow();
        return Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
    }

    private sealed class FailureWindowCounter
    {
        public int Count;
        public DateTimeOffset Expires { get; init; }
    }
}
