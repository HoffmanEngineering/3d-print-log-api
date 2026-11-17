using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Hybrid;
using PrintLogApi.Caching;
using PrintLogApi.Email;
using PrintLogApi.Users;

namespace PrintLogApi.Authentication;

public sealed class ClaimsTransformer(HybridCache cache, CachedComputation computation, TelemetryClient telemetry) : IClaimsTransformation
{
    private static readonly HybridCacheEntryOptions CacheOptions = new()
    {
        Expiration = TimeSpan.FromHours(24),
        LocalCacheExpiration = TimeSpan.FromHours(24),
    };

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        // A principal reaching claims transformation always carries an identity. Casting a
        // null Identity would succeed and then throw on .Claims below, so the null-forgive
        // preserves the existing behaviour exactly.
        var existingClaimsIdentity = (ClaimsIdentity)principal.Identity!;

        var authUserId = existingClaimsIdentity.Claims
            .FirstOrDefault(c => c.Type == ClaimTypes.Upn)?.Value;

        // Fail closed: a token with no subject must not resolve to (or create) a user.
        if (string.IsNullOrWhiteSpace(authUserId))
        {
            return principal;
        }

        // Stampede protection matters more here than at any read-only cache site, because the
        // miss path has a side effect: it can CREATE a user. A first-time login that arrives as
        // several concurrent requests — an SPA opening a session and firing its initial calls
        // together is the ordinary case, not a rare one — previously ran the lookup-then-create
        // once per request, each seeing localUserId == 0 before any of them had committed.
        // GetOrCreateAsync collapses them into one, so the "create" branch runs once.
        //
        // That is a narrowing of an existing race, not a guarantee: this is an L1, in-process
        // cache, so it serializes callers within one instance only. The database's uniqueness
        // constraint on the auth id remains the real defence.
        //
        // The old entry paired a 24h sliding window with a 7-day absolute cap; HybridCache
        // offers absolute expiry only, so this is a flat 24 hours. For a continuously active
        // user that trades a mapping that could live 7 days for one re-read per day — a single
        // indexed lookup, against never letting a stale mapping outlive a deleted user by a
        // week.
        // IUserService is resolved from CachedComputation's scope rather than the injected
        // instance, for the reason set out there: this factory's result is handed to every
        // concurrent caller for this auth id, so it must not run on services torn down when one
        // of those requests ends. That applies with particular force here, where the factory
        // writes a row.
        var cacheKey = $"user_id:{authUserId}";
        var localUserId = await cache.GetOrCreateAsync(
            cacheKey,
            (computation, authUserId),
            static (state, ct) => state.computation.RunAsync(async (services, _) =>
            {
                var users = services.GetRequiredService<IUserService>();

                var existing = await users.GetLocalUserIdByAuthUserId(state.authUserId);
                if (existing != 0)
                {
                    return existing;
                }

                var newUser = await users.CreateUserFromAuthId(state.authUserId);
                return newUser.Id;
            }, ct),
            CacheOptions);

        existingClaimsIdentity.AddClaim(new Claim(ClaimTypes.NameIdentifier, localUserId.ToString()));

        await SyncEmailAsync(existingClaimsIdentity, authUserId, localUserId);

        return principal;
    }

    /// <summary>The (address, verified) pair last written for a user, and the issue time of the token it came from.</summary>
    internal sealed record EmailSyncMark(string Fingerprint, long IssuedAt);

    private static readonly HybridCacheEntryOptions ReadOnly = new()
    {
        Flags = HybridCacheEntryFlags.DisableLocalCacheWrite | HybridCacheEntryFlags.DisableDistributedCacheWrite,
    };

    /// <summary>
    /// Keeps Users.Email in step with the address Auth0 puts in the token. The cache remembers,
    /// per user, the pair last written and the <c>iat</c> of the token that carried it, so an
    /// unchanged pair costs a cache hit and no database round trip, a changed one syncs on the
    /// next request, and an older token still in use on another device (or a change reverted
    /// within the day) can never overwrite what a newer token wrote.
    /// </summary>
    private async Task SyncEmailAsync(ClaimsIdentity identity, string authUserId, long localUserId)
    {
        var email = identity.FindFirst(EmailClaims.Email)?.Value;
        if (string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        var verified = bool.TryParse(identity.FindFirst(EmailClaims.EmailVerified)?.Value, out var v) && v;
        var issuedAt = long.TryParse(identity.FindFirst("iat")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var iat) ? iat : 0;

        // Hashed so the address never appears in the cache, which can surface in logs.
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{email}|{verified}")));
        var key = $"email-sync:{authUserId}";

        try
        {
            var last = await cache.GetOrCreateAsync<EmailSyncMark?>(key, static _ => ValueTask.FromResult<EmailSyncMark?>(null), ReadOnly);
            if (last is not null && (last.Fingerprint == fingerprint || last.IssuedAt > issuedAt))
            {
                return;
            }

            await computation.RunAsync(async (services, token) =>
                await services.GetRequiredService<IUserEmailSyncService>().SyncAsync(localUserId, email, verified, token),
                CancellationToken.None);
            await cache.SetAsync(key, new EmailSyncMark(fingerprint, issuedAt), CacheOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Email is a side channel: a failed sync must never fail the request it rode in on.
            // Nothing is cached on failure, so the next request retries.
            telemetry.TrackEvent("EmailSync_Failed", new Dictionary<string, string> { ["Error"] = ex.GetType().Name });
            telemetry.TrackException(ex);
        }
    }
}
