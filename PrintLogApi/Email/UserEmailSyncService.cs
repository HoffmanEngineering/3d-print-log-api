using Microsoft.EntityFrameworkCore;

namespace PrintLogApi.Email;

public interface IUserEmailSyncService
{
    /// <summary>
    /// Copies the address and verified flag from the user's token onto their row, never clearing
    /// the flag while the address is unchanged. Returns true only when something changed and was
    /// written.
    /// </summary>
    Task<bool> SyncAsync(long userId, string? email, bool verified, CancellationToken ct);
}

public sealed class UserEmailSyncService(PrintLogContext context, TimeProvider timeProvider) : IUserEmailSyncService
{
    public async Task<bool> SyncAsync(long userId, string? email, bool verified, CancellationToken ct)
    {
        // No claim means the token predates the Auth0 Action, not that the address was removed.
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var trimmed = email.Trim();
        var now = timeProvider.GetUtcNow();

        // One conditional UPDATE: no read, and a row already holding these values is untouched,
        // so EmailUpdatedAt records the last real change rather than the last login.
        //
        // The claim lags until the next sign-in, so an unverified claim for the address already
        // stored is stale, not a downgrade: the refresher may have learned from Auth0 since the
        // token was minted. Only a changed address clears the flag. Both SET expressions read the
        // row's values from before the update.
        var updated = await context.Users
            .Where(u => u.Id == userId && (u.Email != trimmed || (verified && !u.EmailVerified)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.Email, trimmed)
                .SetProperty(u => u.EmailVerified, u => verified || (u.Email == trimmed && u.EmailVerified))
                .SetProperty(u => u.EmailUpdatedAt, now), ct);

        return updated > 0;
    }
}
