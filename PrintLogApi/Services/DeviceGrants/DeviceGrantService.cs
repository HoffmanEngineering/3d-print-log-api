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

/// <summary>
/// The Auth0 token a pairing spends: the hash it is remembered by (<see cref="PairingTokenUse"/>)
/// and when that memory may be dropped.
/// </summary>
public sealed record PairingTokenSpend(string KeyHash, DateTime ExpiresAt);

/// <summary>A grant that was revoked, for the audit event.</summary>
public sealed record RevokedDeviceGrant(long Id, string? ClientId, string Scopes);

public sealed class DeviceGrantLimitException : Exception;

public sealed class PairingTokenUsedException : Exception;

/// <summary>The user revoked the device while it was re-pairing; the pairing token is spent.</summary>
public sealed class DeviceGrantRevokedException : Exception;

public interface IDeviceGrantService
{
    /// <summary>
    /// Pairs (or re-pairs) a device and spends <paramref name="pairingToken"/> in the same save.
    /// Throws <see cref="PairingTokenUsedException"/> when that token was spent before, and
    /// <see cref="DeviceGrantLimitException"/> when the user has too many devices, and
    /// <see cref="DeviceGrantRevokedException"/> (token spent) when the user revoked the device
    /// while it re-paired.
    /// </summary>
    Task<PairedDevice> PairAsync(long userId, Guid deviceId, string name, IReadOnlyList<string> scopes, string? clientId, PairingTokenSpend pairingToken, CancellationToken ct);

    Task<IReadOnlyList<DeviceGrant>> ListAsync(long userId, CancellationToken ct);

    /// <summary>Deletes the user's grant and returns what it was, or null when there is none with that id for this user.</summary>
    Task<RevokedDeviceGrant?> RevokeAsync(long userId, long grantId, CancellationToken ct);

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

    public Task<PairedDevice> PairAsync(long userId, Guid deviceId, string name, IReadOnlyList<string> scopes, string? clientId, PairingTokenSpend pairingToken, CancellationToken ct)
        => PairAsync(userId, deviceId, name, scopes, clientId, pairingToken, ct, retried: false);

    private async Task<PairedDevice> PairAsync(long userId, Guid deviceId, string name, IReadOnlyList<string> scopes, string? clientId, PairingTokenSpend pairingToken, CancellationToken ct, bool retried)
    {
        var now = clock.GetUtcNow().UtcDateTime;

        // Spent tokens are only worth remembering while they could still pass the age check, so
        // the table holds about ten minutes of pairings. One indexed delete per pairing keeps it
        // there with no background job.
        await context.PairingTokenUses.Where(u => u.ExpiresAt < now).ExecuteDeleteAsync(ct);

        if (await IsSpentAsync(pairingToken, ct))
        {
            throw new PairingTokenUsedException();
        }

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

        // Spent in the same save as the grant: a pairing that fails leaves the token unspent, and
        // two exchanges of one token cannot both commit, whichever instance serves them.
        context.PairingTokenUses.Add(new PairingTokenUse { TokenKeyHash = pairingToken.KeyHash, ExpiresAt = pairingToken.ExpiresAt });

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            context.ChangeTracker.Clear();

            // The unique key on the spent token is what loses a race between two exchanges of it.
            if (await IsSpentAsync(pairingToken, ct))
            {
                throw new PairingTokenUsedException();
            }

            if (ex is DbUpdateConcurrencyException && !created)
            {
                // The rotation updated no row: the user revoked this device after we read the
                // grant. Never recreate it (that would undo the revoke), and spend the token, so
                // an approval given before the revoke cannot pair the device back on a retry.
                // The user re-approves through a new device flow.
                await SpendAloneAsync(pairingToken, ct);
                throw new DeviceGrantRevokedException();
            }

            if (!created || retried)
            {
                throw;
            }

            // Two first pairings of one device, with two different tokens, raced and the other
            // inserted the (UserId, DeviceId) row. Pair again, which now rotates that row: the
            // device keeps whichever token it receives last.
            return await PairAsync(userId, deviceId, name, scopes, clientId, pairingToken, ct, retried: true);
        }

        return new PairedDevice(grant, token, created);
    }

    private async Task SpendAloneAsync(PairingTokenSpend pairingToken, CancellationToken ct)
    {
        context.PairingTokenUses.Add(new PairingTokenUse { TokenKeyHash = pairingToken.KeyHash, ExpiresAt = pairingToken.ExpiresAt });
        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another exchange of the same token spent it first; that one answers for it.
            context.ChangeTracker.Clear();
            throw new PairingTokenUsedException();
        }
    }

    private Task<bool> IsSpentAsync(PairingTokenSpend pairingToken, CancellationToken ct) =>
        context.PairingTokenUses.AnyAsync(u => u.TokenKeyHash == pairingToken.KeyHash, ct);

    public async Task<IReadOnlyList<DeviceGrant>> ListAsync(long userId, CancellationToken ct) =>
        await context.DeviceGrants
            .AsNoTracking()
            .Where(g => g.UserId == userId)
            .OrderByDescending(g => g.CreatedDate)
            .ThenByDescending(g => g.Id)
            .ToListAsync(ct);

    public async Task<RevokedDeviceGrant?> RevokeAsync(long userId, long grantId, CancellationToken ct)
    {
        var grant = await context.DeviceGrants
            .AsNoTracking()
            .Where(g => g.Id == grantId && g.UserId == userId)
            .Select(g => new RevokedDeviceGrant(g.Id, g.ClientId, g.Scopes))
            .SingleOrDefaultAsync(ct);

        var deleted = await DeleteGrantAsync(userId, grantId, ct);

        // Nothing caches the grant itself; the throttle flag only suppresses a last-used write.
        cache.Remove(LastUsedThrottleKey(grantId));
        return deleted > 0 ? grant : null;
    }

    /// <summary>
    /// Deletes the grant, and with it (a database cascade) its bridge and everything the bridge
    /// found or was told to watch (printlog-bridge#100). Its connections are kept, unlinked first:
    /// their FK has no cascade (SQL Server 1785). A heartbeat can link a connection again between
    /// the two statements, which fails the delete on that FK; the whole step is then retried.
    /// </summary>
    private async Task<int> DeleteGrantAsync(long userId, long grantId, CancellationToken ct)
    {
        const int attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var strategy = context.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await context.Database.BeginTransactionAsync(ct);
                    await context.Connections
                        .Where(c => c.BridgeId == grantId && c.UserId == userId)
                        .ExecuteUpdateAsync(s => s.SetProperty(c => c.BridgeId, (long?)null), ct);
                    var deleted = await context.DeviceGrants
                        .Where(g => g.Id == grantId && g.UserId == userId)
                        .ExecuteDeleteAsync(ct);
                    await transaction.CommitAsync(ct);
                    return deleted;
                });
            }
            catch (System.Data.Common.DbException) when (attempt < attempts)
            {
            }
        }
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
