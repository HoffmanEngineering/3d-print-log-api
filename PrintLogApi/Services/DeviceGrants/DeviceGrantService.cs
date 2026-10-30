using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PrintLogApi.Authentication.Devices;
using PrintLogApi.Caching;
using PrintLogApi.Models;
using PrintLogApi.Services.Bridges;

namespace PrintLogApi.Services.DeviceGrants;

/// <summary>
/// What a valid device token resolves to, on every device-authenticated request.
/// <paramref name="Superseded"/> is true for a token a re-pair replaced, accepted only during
/// <see cref="DeviceGrantService.RepairGrace"/> (api#287).
/// </summary>
public sealed record DeviceGrantIdentity(long GrantId, long UserId, IReadOnlyList<string> Scopes, bool Superseded = false);

/// <summary>
/// The outcome of pairing: the grant, the token (shown this once), whether the row is new, and
/// whether this answered a retry of a pairing that had already succeeded (api#287).
/// </summary>
public sealed record PairedDevice(DeviceGrant Grant, string Token, bool Created, bool Replayed = false);

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
    /// A token already spent by this device's current grant is a retry of that pairing and is
    /// answered again with a fresh token (<see cref="PairedDevice.Replayed"/>). Throws
    /// <see cref="PairingTokenUsedException"/> when the token was spent otherwise,
    /// <see cref="DeviceGrantLimitException"/> when the user has too many devices, and
    /// <see cref="DeviceGrantRevokedException"/> (token spent) when the user revoked the device
    /// while it re-paired.
    /// </summary>
    Task<PairedDevice> PairAsync(long userId, Guid deviceId, string name, IReadOnlyList<string> scopes, string? clientId, PairingTokenSpend pairingToken, CancellationToken ct);

    Task<IReadOnlyList<DeviceGrant>> ListAsync(long userId, CancellationToken ct);

    /// <summary>Deletes the user's grant and returns what it was, or null when there is none with that id for this user.</summary>
    Task<RevokedDeviceGrant?> RevokeAsync(long userId, long grantId, CancellationToken ct);

    /// <summary>
    /// Deletes every grant of the user (api#287), each with what <see cref="RevokeAsync"/> does,
    /// in one transaction. Returns how many were deleted and what they were, for the audit events.
    /// </summary>
    Task<(int Count, IReadOnlyList<RevokedDeviceGrant> Grants)> RevokeAllAsync(long userId, CancellationToken ct);

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

    /// <summary>
    /// How long a re-pair keeps the replaced token working when the new one is never used
    /// (api#287). The new token's first use ends it sooner. It matches the pairing token's own
    /// lifetime (<see cref="DevicePairingOptions.MaxPairingTokenAgeMinutes"/>): a device whose
    /// reply was lost can retry that exchange for about as long, and keeps working meanwhile.
    /// </summary>
    public static readonly TimeSpan RepairGrace = TimeSpan.FromMinutes(10);

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

        var grant = await context.DeviceGrants.SingleOrDefaultAsync(g => g.UserId == userId && g.DeviceId == deviceId, ct);

        // A spent token is refused, except by the grant it paired: that is a retry of a pairing
        // that succeeded but whose reply never arrived (api#287), answered again with a fresh
        // token. Binding to the grant id (nulled by a revoke) rather than the device id is what
        // keeps an approval given before a revoke from ever pairing the device back, even after
        // the user paired it again with a new approval.
        var spend = await SpendOfAsync(pairingToken, ct);
        if (spend is not null && (grant is null || spend.GrantId != grant.Id))
        {
            throw new PairingTokenUsedException();
        }

        var replay = spend is not null;
        var token = DeviceTokenFormat.NewToken();
        var tokenHash = DeviceTokenFormat.Hash(token);
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
        else
        {
            Supersede(grant, now);
        }

        grant.TokenHash = tokenHash;
        grant.Name = name;
        grant.Scopes = DeviceScopes.Join(scopes);
        grant.ClientId = clientId;
        grant.PairedDate = now;

        if (!created && await context.Bridges.SingleOrDefaultAsync(b => b.Id == grant.Id, ct) is { } bridge)
        {
            // The old token is on its way out with this save (it lives on for RepairGrace at
            // most, and a check-in with it cannot report a key), so a key it reported (perhaps a
            // thief's) must not stay confirmable. The re-paired bridge reports its key again, for
            // the owner to confirm (docs/bridge-credential-sealing.md, Key changes).
            BridgeService.ClearPendingKey(bridge);
        }

        if (!replay)
        {
            // Spent in the same save as the grant: a pairing that fails leaves the token unspent,
            // and two exchanges of one token cannot both commit, whichever instance serves them.
            context.PairingTokenUses.Add(new PairingTokenUse { TokenKeyHash = pairingToken.KeyHash, ExpiresAt = pairingToken.ExpiresAt, Grant = grant });
        }

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            context.ChangeTracker.Clear();

            // The save may have committed with its acknowledgment lost, which is what an EF
            // execution-strategy retry then runs into (the second insert of the spend fails on
            // its unique key). The token generated here being the grant's current one proves it.
            if (await context.DeviceGrants.AsNoTracking()
                    .SingleOrDefaultAsync(g => g.UserId == userId && g.DeviceId == deviceId && g.TokenHash == tokenHash, ct) is { } committed)
            {
                return new PairedDevice(committed, token, created, replay);
            }

            if (await SpendOfAsync(pairingToken, ct) is { } spent)
            {
                if (!replay && !retried && spent.GrantId is not null)
                {
                    // Another exchange of this same token won the unique key. If it paired this
                    // device, this is answered as its retry; the replay check on the rerun decides.
                    return await PairAsync(userId, deviceId, name, scopes, clientId, pairingToken, ct, retried: true);
                }

                if (replay && ex is DbUpdateConcurrencyException)
                {
                    // A retry that lost a race with a revoke: the pairing it retried spent the
                    // token, and the grant is gone. Never recreate it.
                    throw new DeviceGrantRevokedException();
                }

                // The unique key on the spent token is what loses a race between two exchanges of it.
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

        return new PairedDevice(grant, token, created, replay);
    }

    /// <summary>
    /// Keeps the token a re-pair replaces working for <see cref="RepairGrace"/> (api#287). While
    /// a grace is already running its token stays the previous one, with its deadline: the token
    /// in between was never used, so the device most likely never received it, and re-pairing
    /// again cannot stretch the deadline.
    /// </summary>
    private static void Supersede(DeviceGrant grant, DateTime now)
    {
        if (grant.PreviousTokenHash is not null && grant.PreviousTokenExpiresAt > now)
        {
            return;
        }

        grant.PreviousTokenHash = grant.TokenHash;
        grant.PreviousTokenExpiresAt = now + RepairGrace;
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

    private sealed record SpentToken(long? GrantId);

    private async Task<SpentToken?> SpendOfAsync(PairingTokenSpend pairingToken, CancellationToken ct) =>
        await context.PairingTokenUses
            .AsNoTracking()
            .Where(u => u.TokenKeyHash == pairingToken.KeyHash)
            .Select(u => new SpentToken(u.GrantId))
            .SingleOrDefaultAsync(ct);

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

        var deleted = await DeleteGrantsAsync(userId, grantId, ct);

        // Nothing caches the grant itself; the throttle flag only suppresses a last-used write.
        cache.Remove(LastUsedThrottleKey(grantId));
        return deleted > 0 ? grant : null;
    }

    public async Task<(int Count, IReadOnlyList<RevokedDeviceGrant> Grants)> RevokeAllAsync(long userId, CancellationToken ct)
    {
        var grants = await context.DeviceGrants
            .AsNoTracking()
            .Where(g => g.UserId == userId)
            .Select(g => new RevokedDeviceGrant(g.Id, g.ClientId, g.Scopes))
            .ToListAsync(ct);

        // Deleted by user, not by these ids: a device that paired after the read goes too.
        var deleted = await DeleteGrantsAsync(userId, grantId: null, ct);

        foreach (var grant in grants)
        {
            cache.Remove(LastUsedThrottleKey(grant.Id));
        }

        return (deleted, grants);
    }

    /// <summary>
    /// Deletes one grant of the user, or all of them when <paramref name="grantId"/> is null, and
    /// with each (a database cascade) its bridge and everything the bridge found or was told to
    /// watch, sealed credentials included (printlog-bridge#100). Their connections are kept,
    /// unlinked first: that FK has no cascade (SQL Server 1785). A heartbeat can link a connection
    /// again between the two statements, which fails the delete on that FK; the whole step is
    /// then retried. Deleting the row ends its current and any superseded token at once.
    /// </summary>
    private async Task<int> DeleteGrantsAsync(long userId, long? grantId, CancellationToken ct)
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
                    var connections = context.Connections.Where(c => c.UserId == userId && c.BridgeId != null);
                    var grants = context.DeviceGrants.Where(g => g.UserId == userId);
                    if (grantId is { } id)
                    {
                        connections = connections.Where(c => c.BridgeId == id);
                        grants = grants.Where(g => g.Id == id);
                    }

                    await connections.ExecuteUpdateAsync(s => s.SetProperty(c => c.BridgeId, (long?)null), ct);
                    var deleted = await grants.ExecuteDeleteAsync(ct);
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
            .Select(g => new { g.Id, g.UserId, g.Scopes, g.PreviousTokenHash })
            .SingleOrDefaultAsync(ct);

        if (row is not null)
        {
            if (row.PreviousTokenHash is not null)
            {
                // The new token's first use proves the device has it, which ends the replaced
                // token's grace (api#287): one conditional write, once per re-pair.
                await context.DeviceGrants
                    .Where(g => g.Id == row.Id && g.TokenHash == hash && g.PreviousTokenHash != null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(g => g.PreviousTokenHash, (string?)null)
                        .SetProperty(g => g.PreviousTokenExpiresAt, (DateTime?)null), ct);
            }

            return new DeviceGrantIdentity(row.Id, row.UserId, DeviceScopes.Split(row.Scopes));
        }

        // A second indexed read, only for a token that is not current: a re-pair's replaced
        // token inside its grace. Kept apart from the first so neither depends on the plan SQL
        // Server picks for an OR across two indexes.
        var previous = await context.DeviceGrants
            .AsNoTracking()
            .Where(g => g.PreviousTokenHash == hash)
            .Select(g => new { g.Id, g.UserId, g.Scopes, g.PreviousTokenExpiresAt })
            .FirstOrDefaultAsync(ct);

        return previous is { PreviousTokenExpiresAt: { } until } && until > clock.GetUtcNow().UtcDateTime
            ? new DeviceGrantIdentity(previous.Id, previous.UserId, DeviceScopes.Split(previous.Scopes), Superseded: true)
            : null;
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
