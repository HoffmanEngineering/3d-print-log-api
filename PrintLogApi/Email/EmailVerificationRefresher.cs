using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PrintLogApi.Caching;
using PrintLogApi.Services;

namespace PrintLogApi.Email;

public interface IEmailVerificationRefresher
{
    /// <summary>
    /// Returns the user's effective email-verified flag, asking Auth0 when the database still
    /// says unverified (the token claim lags until the user signs in again). An Auth0 call is
    /// throttled to one per user per <see cref="EmailVerificationRefresher.Throttle"/>;
    /// <paramref name="force"/> skips that check (for callers with their own limit) but still
    /// sets the throttle. Never throws, except when <paramref name="ct"/> is canceled.
    /// </summary>
    Task<bool> RefreshAsync(long userId, CancellationToken ct, bool force = false);
}

public sealed class EmailVerificationRefresher(
    PrintLogContext db,
    IAuth0Service auth0,
    IMemoryCache cache,
    ILogger<EmailVerificationRefresher> logger,
    TimeProvider clock) : IEmailVerificationRefresher
{
    /// <summary>How long an unforced call waits after any Auth0 status call for the same user.</summary>
    public static readonly TimeSpan Throttle = TimeSpan.FromMinutes(55);

    public async Task<bool> RefreshAsync(long userId, CancellationToken ct, bool force = false)
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
        if (!force && cache.TryGetValue(key, out _))
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
            .SetAbsoluteExpiration(Throttle));

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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The request went away, so nothing was learned: let the next one ask.
            cache.Remove(key);
            Forget(user);
            throw;
        }
        catch (Exception ex)
        {
            // Includes HttpClient's timeout, a TaskCanceledException while ct is still live.
            logger.LogWarning(ex, "Email verification refresh failed for user {UserId}", userId);
            Forget(user);
            return false;
        }
    }

    // The context can be shared (the dispatcher runs a whole tick on one), so an unsaved change
    // must not be left behind for that caller's next SaveChanges to retry.
    private void Forget(Models.User user) => db.Entry(user).State = EntityState.Detached;
}
