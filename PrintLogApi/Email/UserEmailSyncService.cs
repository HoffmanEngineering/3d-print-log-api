using Microsoft.EntityFrameworkCore;

namespace PrintLogApi.Email;

public interface IUserEmailSyncService
{
    /// <summary>
    /// Copies the address and verified flag from the user's token onto their row. Returns true
    /// only when something changed and was written.
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
        var updated = await context.Users
            .Where(u => u.Id == userId && (u.Email != trimmed || u.EmailVerified != verified))
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.Email, trimmed)
                .SetProperty(u => u.EmailVerified, verified)
                .SetProperty(u => u.EmailUpdatedAt, now), ct);

        return updated > 0;
    }
}
