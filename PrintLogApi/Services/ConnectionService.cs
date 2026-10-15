using Microsoft.EntityFrameworkCore;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Connection;

namespace PrintLogApi.Services;

public class ConnectionService(PrintLogContext context, TimeProvider clock) : IConnectionService
{
    public async Task<(Connection Connection, bool Created)> Upsert(long userId, string instanceId, PutConnectionDto dto)
    {
        if (dto.PrinterId is { } printerId &&
            !await context.Printers.AnyAsync(p => p.Id == printerId && p.UserId == userId))
        {
            throw new UserCannotAccessPrinterException();
        }

        try
        {
            return await UpsertOnce(userId, instanceId, dto);
        }
        catch (DbUpdateException)
        {
            // Lost the insert race on (UserId, InstanceId): an agent's first heartbeat retried
            // while the first was still in flight. The row exists now, so take the update path.
            context.ChangeTracker.Clear();
            return await UpsertOnce(userId, instanceId, dto);
        }
    }

    private async Task<(Connection, bool)> UpsertOnce(long userId, string instanceId, PutConnectionDto dto)
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
        connection.LastSeenAt = now;
        ApplyHealth(connection, dto, now);

        await context.SaveChangesAsync();
        return (connection, created);
    }

    /// <summary>
    /// Replaces the connection's health with what this heartbeat sent (printlog-bridge#37). Cut
    /// and clamped rather than validated: an agent must never lose its heartbeat over health.
    /// </summary>
    private static void ApplyHealth(Connection connection, PutConnectionDto dto, DateTime now)
    {
        connection.Mode = Cut(dto.Mode, 20);
        connection.KlipperVersion = Cut(dto.KlipperVersion, 50);
        connection.MoonrakerVersion = Cut(dto.MoonrakerVersion, 50);
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
