using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;

namespace PrintLogApi.Services;

public class DeviceTokenService(PrintLogContext context, ILogger<DeviceTokenService> logger)
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

    public async Task<IReadOnlyList<string>> GetTokensForUser(long userId)
        => await context.DeviceTokens
            .Where(d => d.UserId == userId)
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
