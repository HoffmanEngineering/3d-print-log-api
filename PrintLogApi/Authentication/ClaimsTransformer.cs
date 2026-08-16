using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Hybrid;
using PrintLogApi.Caching;
using PrintLogApi.Users;

namespace PrintLogApi.Authentication;

public sealed class ClaimsTransformer(HybridCache cache, CachedComputation computation) : IClaimsTransformation
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

        return principal;
    }
}
