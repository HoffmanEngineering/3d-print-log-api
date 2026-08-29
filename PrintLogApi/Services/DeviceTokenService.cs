using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrintLogApi.Models;
using PrintLogApi.Services.Push;

namespace PrintLogApi.Services;

public class DeviceTokenService(
    PrintLogContext context,
    IOptions<PushOptions> pushOptions,
    ILogger<DeviceTokenService> logger)
    : IDeviceTokenService
{
    public async Task RegisterDevice(long userId, string token, DevicePlatform platform, string? appVersion)
    {
        try
        {
            await UpsertOnce(userId, token, platform, appVersion);
        }
        catch (DbUpdateException)
        {
            // Lost an insert race with another request for the same token — two devices
            // registering at once, or a retry. The row now exists, so re-read and take the
            // update path. Registration happens on every app launch; it must not 500.
            context.ChangeTracker.Clear();
            await UpsertOnce(userId, token, platform, appVersion);
        }
    }

    private async Task UpsertOnce(long userId, string token, DevicePlatform platform, string? appVersion)
    {
        var now = DateTime.UtcNow;
        var existing = await context.DeviceTokens.SingleOrDefaultAsync(d => d.Token == token);

        if (existing is null)
        {
            await EvictOldestBeyondCap(userId);

            context.DeviceTokens.Add(new DeviceToken
            {
                UserId = userId,
                Token = token,
                Platform = platform,
                AppVersion = appVersion,
                CreatedDate = now,
                LastSeenDate = now
            });
        }
        else
        {
            if (existing.UserId != userId)
            {
                logger.LogWarning(
                    "Device token reassigned from user {PreviousUserId} to user {NewUserId}.",
                    existing.UserId, userId);
                existing.UserId = userId;
            }

            existing.Platform = platform;
            existing.AppVersion = appVersion;
            existing.LastSeenDate = now;
        }

        await context.SaveChangesAsync();
    }

    public async Task<bool> RemoveDevice(long userId, string token)
    {
        var existing = await context.DeviceTokens
            .SingleOrDefaultAsync(d => d.Token == token && d.UserId == userId);

        if (existing is null)
        {
            return false;
        }

        context.DeviceTokens.Remove(existing);
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Keeps a user's registrations under <see cref="PushOptions.MaxDevicesPerUser"/> by
    /// dropping the least recently seen rows, so the newest install always registers.
    /// </summary>
    /// <remarks>
    /// Rejecting the new device instead would mean a user who reinstalls repeatedly is locked
    /// out of push by their own stale rows, which is the worse failure: the caller is a real
    /// app launch, and the rows it displaces are installations that have not been seen since.
    /// </remarks>
    private async Task EvictOldestBeyondCap(long userId)
    {
        var cap = pushOptions.Value.MaxDevicesPerUser;
        if (cap <= 0)
        {
            return;
        }

        var surplus = await context.DeviceTokens
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.LastSeenDate)
            .Skip(cap - 1)
            .ToListAsync();

        if (surplus.Count == 0)
        {
            return;
        }

        logger.LogInformation(
            "Evicting {Count} device token(s) for user {UserId} over the {Cap}-device cap.",
            surplus.Count, userId, cap);

        context.DeviceTokens.RemoveRange(surplus);
    }

    // Capped for the same reason the registration path is: this list becomes one provider
    // message each, and FirebaseAdmin documents SendEachAsync as taking at most 500. The cap
    // is enforced on write, so this is a backstop for rows that predate it.
    public async Task<IReadOnlyList<string>> GetTokensForUser(long userId)
        => await context.DeviceTokens
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.LastSeenDate)
            .Take(Math.Clamp(pushOptions.Value.MaxDevicesPerUser, 1, 500))
            .Select(d => d.Token)
            .ToListAsync();

    public async Task PruneTokens(IEnumerable<string> tokens)
    {
        var list = tokens.ToList();
        if (list.Count == 0)
        {
            return;
        }

        await context.DeviceTokens
            .Where(d => list.Contains(d.Token))
            .ExecuteDeleteAsync();
    }
}
