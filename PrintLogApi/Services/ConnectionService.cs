using Microsoft.EntityFrameworkCore;
using PrintLogApi.Exceptions;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Connection;

namespace PrintLogApi.Services;

public class ConnectionService(PrintLogContext context, TimeProvider clock) : IConnectionService
{
    public async Task<(Connection Connection, bool Created)> Upsert(long userId, string instanceId, PutConnectionDto dto, long? deviceGrantId = null)
    {
        await RequirePrinter(userId, dto.PrinterId);

        try
        {
            return await UpsertOnce(userId, instanceId, dto, deviceGrantId);
        }
        catch (DbUpdateException ex)
        {
            context.ChangeTracker.Clear();

            // The printer can be deleted between the check above and the save, which fails the
            // FK on PrinterId (#201). Re-checking turns that into the documented 400, not a 500.
            await RequirePrinter(userId, dto.PrinterId);

            // The bridge can be revoked between reading it and the save, which fails the FK on
            // BridgeId; the retry reads it again and records none.
            var bridgeGone = deviceGrantId is { } grantId && !await context.Bridges.AnyAsync(b => b.Id == grantId);
            if (!UniqueConstraintViolation.IsUniqueViolation(ex) && !bridgeGone)
            {
                throw;
            }

            // Lost the insert race on (UserId, InstanceId): an agent's first heartbeat retried
            // while the first was still in flight. The row exists now, so take the update path.
            try
            {
                return await UpsertOnce(userId, instanceId, dto, deviceGrantId);
            }
            catch (DbUpdateException)
            {
                // The printer can also go between the recheck and this save.
                context.ChangeTracker.Clear();
                await RequirePrinter(userId, dto.PrinterId);
                throw;
            }
        }
    }

    private async Task RequirePrinter(long userId, long? printerId)
    {
        if (printerId is { } id &&
            !await context.Printers.AnyAsync(p => p.Id == id && p.UserId == userId))
        {
            throw new UserCannotAccessPrinterException();
        }
    }

    private async Task<(Connection, bool)> UpsertOnce(long userId, string instanceId, PutConnectionDto dto, long? deviceGrantId)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var connection = await context.Connections
            .SingleOrDefaultAsync(c => c.UserId == userId && c.InstanceId == instanceId);
        var created = connection is null;
        if (connection is null)
        {
            connection = new Connection
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                InstanceId = instanceId,
                CreatedDate = now,
            };
            context.Connections.Add(connection);
        }

        // Validation guarantees both are present and not blank.
        connection.Kind = dto.Kind!.Trim().ToLowerInvariant();
        connection.DisplayName = dto.DisplayName!.Trim();
        connection.AgentVersion = dto.AgentVersion?.Trim();
        connection.PrinterId = dto.PrinterId;

        // From the token, never the body: a device whose grant has checked in as a bridge.
        connection.BridgeId = deviceGrantId is { } grantId && await context.Bridges.AnyAsync(b => b.Id == grantId) ? grantId : null;
        connection.LastSeenAt = now;
        ApplyHealth(connection, dto, now);

        await context.SaveChangesAsync();
        return (connection, created);
    }

    /// <summary>
    /// Replaces the connection's health with what this heartbeat sent (printlog-bridge#37). Cut
    /// and clamped rather than validated: an agent must never lose its heartbeat over health.
    /// The software names and versions are the exception: see <see cref="ApplySoftware"/>.
    /// </summary>
    private static void ApplyHealth(Connection connection, PutConnectionDto dto, DateTime now)
    {
        connection.Mode = Cut(dto.Mode, 20);
        ApplySoftware(connection, dto);
        connection.PrinterModel = Cut(dto.PrinterModel, 100);
        connection.OutboxDepth = dto.OutboxDepth is { } depth ? Math.Max(0, depth) : null;
        connection.OutboxOldestQueuedAt = dto.OutboxOldestAgeSeconds is { } age
            ? now - TimeSpan.FromSeconds(Math.Clamp(age, 0, (long)TimeSpan.FromDays(3650).TotalSeconds))
            : null;
        connection.LastError = Cut(dto.LastError, 500);
        connection.LastErrorAt = dto.LastErrorAt is { } at ? Min(at.UtcDateTime, now) : null;
        connection.ErrorCount = dto.ErrorCount is { } count ? Math.Max(0, count) : null;
        connection.Snapshots = dto.Snapshots;
        connection.SlicerSettings = dto.SlicerSettings;
    }

    /// <summary>
    /// The firmware and host software, in both shapes: the generic fields (printlog-bridge#82) and
    /// Klipper's and Moonraker's own, each filled from the other so an old bridge and the current
    /// web app keep working. A name or version that is missing, null or blank keeps the stored
    /// one (#80): a bridge's first check-in can go out before it has read them, and they do not
    /// stop being true because one heartbeat did not know them.
    /// </summary>
    private static void ApplySoftware(Connection connection, PutConnectionDto dto)
    {
        var klipper = Cut(dto.KlipperVersion, 50);
        var moonraker = Cut(dto.MoonrakerVersion, 50);
        var (firmwareName, firmwareVersion) = Generic(dto.FirmwareName, dto.FirmwareVersion, "Klipper", klipper);
        var (hostName, hostVersion) = Generic(dto.HostSoftwareName, dto.HostSoftwareVersion, "Moonraker", moonraker);

        connection.FirmwareName = firmwareName ?? connection.FirmwareName;
        connection.FirmwareVersion = firmwareVersion ?? connection.FirmwareVersion;
        connection.HostSoftwareName = hostName ?? connection.HostSoftwareName;
        connection.HostSoftwareVersion = hostVersion ?? connection.HostSoftwareVersion;
        connection.KlipperVersion = klipper ?? Named(firmwareName, "Klipper", firmwareVersion) ?? connection.KlipperVersion;
        connection.MoonrakerVersion = moonraker ?? Named(hostName, "Moonraker", hostVersion) ?? connection.MoonrakerVersion;

        // The generic pair as sent, or the legacy field under its own name when no version was sent.
        static (string? Name, string? Version) Generic(string? name, string? version, string legacyName, string? legacyVersion)
        {
            var cutName = Cut(name, 50);
            var cutVersion = Cut(version, 50);
            return cutVersion is null && legacyVersion is not null
                ? (cutName ?? legacyName, legacyVersion)
                : (cutName, cutVersion);
        }

        static string? Named(string? name, string expected, string? version) =>
            string.Equals(name, expected, StringComparison.OrdinalIgnoreCase) ? version : null;
    }

    private static string? Cut(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    public async Task<IReadOnlyList<Connection>> List(long userId, long? printerId)
        => await context.Connections
            .Where(c => c.UserId == userId && (printerId == null || c.PrinterId == printerId))
            .OrderBy(c => c.DisplayName)
            .ThenBy(c => c.InstanceId)
            .AsNoTracking()
            .ToListAsync();

    public Task<Connection?> Get(long userId, string instanceId)
        => context.Connections
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.UserId == userId && c.InstanceId == instanceId);

    public async Task<bool> DismissNotifierNotice(long userId, string instanceId)
    {
        var connection = await context.Connections
            .SingleOrDefaultAsync(c => c.UserId == userId && c.InstanceId == instanceId);
        if (connection is null)
        {
            return false;
        }

        if (connection.NotifierNoticeDismissedAt is null)
        {
            connection.NotifierNoticeDismissedAt = clock.GetUtcNow().UtcDateTime;
            await context.SaveChangesAsync();
        }
        return true;
    }

    public async Task<bool> Delete(long userId, string instanceId)
    {
        var connection = await context.Connections
            .SingleOrDefaultAsync(c => c.UserId == userId && c.InstanceId == instanceId);
        if (connection is null)
        {
            return false;
        }

        // The FK does not set null in the database (see PrintLogContext), so unlink the prints
        // first. One transaction, so a failed delete keeps the links; inside the execution
        // strategy because a retrying strategy refuses a transaction opened outside it.
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            await context.Prints
                .Where(p => p.ConnectionId == connection.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.ConnectionId, (Guid?)null));
            context.Connections.Remove(connection);
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        });
        return true;
    }
}
