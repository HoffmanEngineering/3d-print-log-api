using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PrintLogApi.Authentication.Devices;
using PrintLogApi.Caching;
using PrintLogApi.Models;

namespace PrintLogApi.Services.DeviceGrants;

/// <summary>What a valid device token resolves to, on every device-authenticated request.</summary>
public sealed record DeviceGrantIdentity(long GrantId, long UserId, IReadOnlyList<string> Scopes);

/// <summary>The outcome of pairing: the grant, the token (shown this once), and whether the row is new.</summary>
public sealed record PairedDevice(DeviceGrant Grant, string Token, bool Created);

public sealed class DeviceGrantLimitException : Exception;

public interface IDeviceGrantService
{
    Task<PairedDevice> PairAsync(long userId, Guid deviceId, string name, IReadOnlyList<string> scopes, string? clientId, CancellationToken ct);

    Task<IReadOnlyList<DeviceGrant>> ListAsync(long userId, CancellationToken ct);

    /// <summary>Deletes the user's grant. False when there is none with that id for this user.</summary>
    Task<bool> RevokeAsync(long userId, long grantId, CancellationToken ct);

    /// <summary>The grant a token belongs to, or null.</summary>
    Task<DeviceGrantIdentity?> AuthenticateAsync(string token, CancellationToken ct);

    Task TouchLastUsedAsync(long grantId, CancellationToken ct);
}

/// <summary>
/// Device grants (#148). The token lookup is deliberately uncached: it is one indexed read per
/// device request, and caching it would make revocation wait for an eviction that only reaches
/// the instance that served the revoke. Bridge traffic is a heartbeat per printer every five
/// minutes plus the prints, so there is nothing to save.
/// </summary>
public sealed class DeviceGrantService(
    PrintLogContext context,
    IMemoryCache cache,
    IOptions<DevicePairingOptions> options,
    TimeProvider clock) : IDeviceGrantService
{
    /// <summary>How often a grant's last-used time is written; within it, uses are not recorded.</summary>
    public static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(5);

    private static string LastUsedThrottleKey(long grantId) => $"devicegrant_lastused:{grantId}";

    public Task<PairedDevice> PairAsync(long userId, Guid deviceId, string name, IReadOnlyList<string> scopes, string? clientId, CancellationToken ct)
        => PairAsync(userId, deviceId, name, scopes, clientId, ct, retried: false);

    private async Task<PairedDevice> PairAsync(long userId, Guid deviceId, string name, IReadOnlyList<string> scopes, string? clientId, CancellationToken ct, bool retried)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var token = DeviceTokenFormat.NewToken();

        var grant = await context.DeviceGrants.SingleOrDefaultAsync(g => g.UserId == userId && g.DeviceId == deviceId, ct);
        var created = grant is null;
        if (grant is null)
        {
            // Counted before the insert, so two racing pairings can each squeeze under the limit
            // by one. The limit bounds abuse of a leaked pairing token; it is not a quota.
            var count = await context.DeviceGrants.CountAsync(g => g.UserId == userId, ct);
            if (count >= options.Value.MaxGrantsPerUser)
            {
                throw new DeviceGrantLimitException();
            }

            grant = new DeviceGrant { UserId = userId, DeviceId = deviceId, CreatedDate = now };
            context.DeviceGrants.Add(grant);
        }

        // A re-pair replaces the token, so a copy of the old one stops working now.
        grant.TokenHash = DeviceTokenFormat.Hash(token);
        grant.Name = name;
        grant.Scopes = DeviceScopes.Join(scopes);
        grant.ClientId = clientId;
        grant.PairedDate = now;

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (created && !retried)
        {
            // Two first pairings of one device raced (a bridge retrying a timed-out request) and
            // the other one inserted the (UserId, DeviceId) row. Pair again, which now rotates
            // that row: the device keeps whichever token it receives last.
            context.ChangeTracker.Clear();
            return await PairAsync(userId, deviceId, name, scopes, clientId, ct, retried: true);
        }

        return new PairedDevice(grant, token, created);
    }

    public async Task<IReadOnlyList<DeviceGrant>> ListAsync(long userId, CancellationToken ct) =>
        await context.DeviceGrants
            .AsNoTracking()
            .Where(g => g.UserId == userId)
            .OrderByDescending(g => g.CreatedDate)
            .ThenByDescending(g => g.Id)
            .ToListAsync(ct);

    public async Task<bool> RevokeAsync(long userId, long grantId, CancellationToken ct)
    {
        var deleted = await context.DeviceGrants
            .Where(g => g.Id == grantId && g.UserId == userId)
            .ExecuteDeleteAsync(ct);

        // Nothing caches the grant itself; the throttle flag only suppresses a last-used write.
        cache.Remove(LastUsedThrottleKey(grantId));
        return deleted > 0;
    }

    public async Task<DeviceGrantIdentity?> AuthenticateAsync(string token, CancellationToken ct)
    {
        // A malformed value cannot match a hash we stored, so it costs no query.
        if (token.Length != DeviceTokenFormat.Length)
        {
            return null;
        }

        var hash = DeviceTokenFormat.Hash(token);
        var row = await context.DeviceGrants
            .AsNoTracking()
            .Where(g => g.TokenHash == hash)
            .Select(g => new { g.Id, g.UserId, g.Scopes })
            .SingleOrDefaultAsync(ct);

        return row is null ? null : new DeviceGrantIdentity(row.Id, row.UserId, DeviceScopes.Split(row.Scopes));
    }

    public async Task TouchLastUsedAsync(long grantId, CancellationToken ct)
    {
        var key = LastUsedThrottleKey(grantId);
        if (cache.TryGetValue(key, out _))
        {
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await context.DeviceGrants
            .Where(g => g.Id == grantId)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.LastUsedDate, now), ct);

        // A throttle flag, not a cache (same category as UserApiKeyService's): its presence is
        // the whole signal. Charged in bytes like everything in the shared budget.
        cache.Set(key, true, new MemoryCacheEntryOptions()
            .SetSize(CacheBudget.SmallEntryBytes)
            .SetAbsoluteExpiration(LastUsedResolution));
    }
}
