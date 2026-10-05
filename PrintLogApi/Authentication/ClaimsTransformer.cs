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

    /// <summary>
    /// Keeps Users.Email in step with the address Auth0 puts in the token, at most once a day per
    /// distinct (address, verified) pair. The pair is part of the cache key, so a changed address
    /// syncs on the next request instead of up to 24 hours later, while an unchanged one costs a
    /// cache hit and no database round trip.
    /// </summary>
    private async Task SyncEmailAsync(ClaimsIdentity identity, string authUserId, long localUserId)
    {
        var email = identity.FindFirst(EmailClaims.Email)?.Value;
        if (string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        var verified = bool.TryParse(identity.FindFirst(EmailClaims.EmailVerified)?.Value, out var v) && v;

        // Hashed so the address never appears in a cache key, which can surface in logs.
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{email}|{verified}")));

        try
        {
            await cache.GetOrCreateAsync(
                $"email-sync:{authUserId}:{fingerprint}",
                (computation, localUserId, email, verified),
                static (state, ct) => state.computation.RunAsync(async (services, token) =>
                {
                    await services.GetRequiredService<IUserEmailSyncService>()
                        .SyncAsync(state.localUserId, state.email, state.verified, token);
                    return true;
                }, ct),
                CacheOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Email is a side channel: a failed sync must never fail the request it rode in on.
            // HybridCache does not cache a throwing factory, so the next request retries.
            telemetry.TrackEvent("EmailSync_Failed", new Dictionary<string, string> { ["Error"] = ex.GetType().Name });
            telemetry.TrackException(ex);
        }
    }
}
