using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PrintLogApi.Caching;
using PrintLogApi.Services;

namespace PrintLogApi.Email;

public interface IEmailVerificationRefresher
{
    /// <summary>
    /// Returns the user's effective email-verified flag, asking Auth0 when the database still
    /// says unverified (the token claim lags until the user signs in again). Never throws.
    /// </summary>
    Task<bool> RefreshAsync(long userId, CancellationToken ct);
}

public sealed class EmailVerificationRefresher(
    PrintLogContext db,
    IAuth0Service auth0,
    IMemoryCache cache,
    ILogger<EmailVerificationRefresher> logger,
    TimeProvider clock) : IEmailVerificationRefresher
{
    public async Task<bool> RefreshAsync(long userId, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return false;
        }

        if (user.EmailVerified)
        {
            return true;
        }

        if (string.IsNullOrEmpty(user.OAuthUserId))
        {
            return false;
        }

        var key = $"email_verify_refresh:{userId}";
        if (cache.TryGetValue(key, out _))
        {
            return false;
        }

        // Deliberately still IMemoryCache, not HybridCache: this is a throttle flag, not a
        // cached computation. Its value carries no information — its presence is the whole
        // signal — so there is no miss to deduplicate and GetOrCreateAsync would only obscure
        // that. Same category as ApiKeyMiddleware's failed-attempt counter. Set before the
        // call so a failing Auth0 is not hammered either.
        cache.Set(key, true, new MemoryCacheEntryOptions()
            .SetSize(CacheBudget.SmallEntryBytes)
            .SetAbsoluteExpiration(TimeSpan.FromHours(1)));

        try
        {
            var status = await auth0.GetEmailStatus(user.OAuthUserId, ct);
            if (status is not { EmailVerified: true })
            {
                return false;
            }

            user.EmailVerified = true;
            var trimmed = status.Email?.Trim();
            if (!string.IsNullOrEmpty(trimmed) && user.Email != trimmed)
            {
                user.Email = trimmed;
            }
            user.EmailUpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Email verification refresh failed for user {UserId}", userId);
            return user.EmailVerified;
        }
    }
}
