using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Services.Bridges;

/// <summary>What the cross-bridge safety net decided for a bridge's new print.</summary>
public enum BridgePrintVerdict
{
    /// <summary>Go ahead as usual.</summary>
    Accept,

    /// <summary>Another bridge is bound to the printer and online: refuse (403) and count it.</summary>
    Refuse,

    /// <summary>The job is already logged by the other bridge: answer with <see cref="BridgePrintDecision.PrintId"/>.</summary>
    UsePrint,
}

public sealed record BridgePrintDecision(BridgePrintVerdict Verdict, long? BoundBridgeId = null, long? PrintId = null, bool HandedOver = false)
{
    public static readonly BridgePrintDecision Accepted = new(BridgePrintVerdict.Accept);
}

public interface IBridgePrintGuard
{
    /// <summary>
    /// Decides a print create (<c>POST /api/Prints</c>) sent with bridge <paramref name="grantId"/>'s
    /// device token. Writes the refusal counter, or the hand-off, itself.
    /// </summary>
    Task<BridgePrintDecision> CheckCreateAsync(long userId, long grantId, AddPrintDTO print, CancellationToken ct);

    /// <summary>Decides a claim (<c>POST /api/Prints/{id}/claim</c>): accepted or refused, never handed over.</summary>
    Task<BridgePrintDecision> CheckClaimAsync(long userId, long grantId, long printId, ClaimPrintDto claim, CancellationToken ct);
}

/// <summary>
/// The cross-bridge safety net (printlog-bridge#103, story 15a). Connect already refuses a second
/// watcher, but bridges that identify one printer by different fields, or a printer in one
/// bridge's config file and another's desired list, can still both watch it, and each logs every
/// print under its own instance id. So a print a bridge starts for a printer bound (by a desired
/// entry) to another of the user's bridges is refused, the way the Moonraker notifier is dropped
/// for a live bridge (#150).
/// <list type="bullet">
/// <item>Only new prints are judged: a replay of an existing pair, and every write to a print that
/// exists (complete, patch, images), go through. A refused create never makes a print, so nothing
/// follows it.</item>
/// <item>Only a bridge is judged: a device token whose grant checked in as a bridge. API keys and
/// other devices name no bridge.</item>
/// <item>The bound bridge must be online (seen within <see cref="Connection.StaleAfter"/>): while
/// it is down nothing else logs the printer, and a duplicate beats a lost print.</item>
/// <item>A job that started before the printer was bound (<see cref="BridgePrinter.MovedAt"/>,
/// else its <see cref="BridgePrinter.CreatedDate"/>) is the old bridge's: accepted, since its
/// outbox may send it late.</item>
/// <item>Hand-off: the old bridge stops watching within a minute of a move and never sees that job
/// end; the new bridge finds it running at first look. When a bridge starts a print for a job that
/// another bridge already logged (same printer, still Printing, start within
/// <see cref="SameJobTolerance"/>, same file name when both have one), it gets that print instead
/// of a second one. The bound bridge takes it over (its pair and connection replace the old
/// bridge's, so its own replays find it); the old bridge just gets it back.</item>
/// </list>
/// </summary>
public sealed class BridgePrintGuard(PrintLogContext context, TimeProvider clock, ICacheVersionService cacheVersions) : IBridgePrintGuard
{
    /// <summary>How far apart two bridges' start times for one job may be (each reads the printer's, or its own clock).</summary>
    public static readonly TimeSpan SameJobTolerance = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long after the move a job the new bridge reports may have started and still be the one
    /// running at the move: the bridge picks the printer up within a poll, and a bridge that can't
    /// read the job's own start time stamps its first look.
    /// </summary>
    public static readonly TimeSpan HandOffGrace = TimeSpan.FromMinutes(10);

    private sealed record Binding(long BridgeId, DateTime BoundSince, DateTime BridgeLastSeen);

    public async Task<BridgePrintDecision> CheckCreateAsync(long userId, long grantId, AddPrintDTO print, CancellationToken ct)
    {
        if (await BindingAsync(userId, grantId, print.PrinterId, ct) is not { } binding)
        {
            return BridgePrintDecision.Accepted;
        }

        if (print.ExternalSource is { } rawSource && print.ExternalId is { } rawId
            && await PairExistsAsync(userId, ExternalPrintIds.NormalizeSource(rawSource), ExternalPrintIds.NormalizeId(rawId), ct))
        {
            return BridgePrintDecision.Accepted;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var started = print.StartDate?.UtcDateTime ?? now;
        if (binding.BridgeId == grantId)
        {
            if (started <= binding.BoundSince + HandOffGrace
                && await SameJobAsync(userId, grantId, print.PrinterId, started, print.FileName, ct) is { } open
                && await TakeOverAsync(userId, open, print, ct))
            {
                return new BridgePrintDecision(BridgePrintVerdict.UsePrint, PrintId: open.Id, HandedOver: true);
            }

            return BridgePrintDecision.Accepted;
        }

        if (started < binding.BoundSince)
        {
            return await SameJobAsync(userId, grantId, print.PrinterId, started, print.FileName, ct) is { } logged
                ? new BridgePrintDecision(BridgePrintVerdict.UsePrint, PrintId: logged.Id)
                : BridgePrintDecision.Accepted;
        }

        return await RefuseIfBoundBridgeOnlineAsync(grantId, print.PrinterId, binding, now, ct);
    }

    public async Task<BridgePrintDecision> CheckClaimAsync(long userId, long grantId, long printId, ClaimPrintDto claim, CancellationToken ct)
    {
        var target = await context.Prints.AsNoTracking()
            .Where(p => p.Id == printId && p.CreatedById == userId)
            .Select(p => new { p.PrinterId, p.ExternalSource, p.ExternalId })
            .SingleOrDefaultAsync(ct);
        if (target is null || await BindingAsync(userId, grantId, target.PrinterId, ct) is not { } binding || binding.BridgeId == grantId)
        {
            return BridgePrintDecision.Accepted;
        }

        // A retried claim of the same pair replays.
        if (claim.ExternalSource is { } rawSource && claim.ExternalId is { } rawId
            && string.Equals(target.ExternalSource, ExternalPrintIds.NormalizeSource(rawSource), StringComparison.OrdinalIgnoreCase)
            && string.Equals(target.ExternalId, ExternalPrintIds.NormalizeId(rawId), StringComparison.OrdinalIgnoreCase))
        {
            return BridgePrintDecision.Accepted;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if ((claim.StartDate?.UtcDateTime ?? now) < binding.BoundSince)
        {
            return BridgePrintDecision.Accepted;
        }

        return await RefuseIfBoundBridgeOnlineAsync(grantId, target.PrinterId, binding, now, ct);
    }

    /// <summary>The printer's desired entry, when the caller is a bridge of the user's and the printer is the user's. Null otherwise.</summary>
    private async Task<Binding?> BindingAsync(long userId, long grantId, long printerId, CancellationToken ct)
    {
        var binding = await context.BridgePrinters.AsNoTracking()
            .Where(p => p.PrinterId == printerId && p.Printer.UserId == userId)
            .Select(p => new { p.BridgeId, p.MovedAt, p.CreatedDate, p.Bridge.LastSeenAt })
            .SingleOrDefaultAsync(ct);
        if (binding is null || !await context.Bridges.AnyAsync(b => b.Id == grantId && b.Grant.UserId == userId, ct))
        {
            return null;
        }

        return new Binding(binding.BridgeId, binding.MovedAt ?? binding.CreatedDate, binding.LastSeenAt);
    }

    private Task<bool> PairExistsAsync(long userId, string source, string externalId, CancellationToken ct) =>
        context.Prints.AnyAsync(p => p.CreatedById == userId && p.ExternalSource == source && p.ExternalId == externalId, ct);

    private async Task<BridgePrintDecision> RefuseIfBoundBridgeOnlineAsync(long grantId, long printerId, Binding binding, DateTime now, CancellationToken ct)
    {
        if (binding.BridgeLastSeen < now - Connection.StaleAfter)
        {
            return BridgePrintDecision.Accepted;
        }

        // One statement, so concurrent refusals can't lose an increment. No cache version: nothing
        // cached reads bridges.
        await context.Bridges
            .Where(b => b.Id == grantId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.DroppedEventCount, b => b.DroppedEventCount + 1)
                .SetProperty(b => b.LastDroppedEventAt, now)
                .SetProperty(b => b.LastDroppedPrinterId, printerId), ct);
        return new BridgePrintDecision(BridgePrintVerdict.Refuse, BoundBridgeId: binding.BridgeId);
    }

    private sealed record OpenPrint(long Id, string? ExternalSource, string? ExternalId);

    /// <summary>
    /// The printer's open print that another bridge logged for the same job, or null. A printer
    /// runs one job at a time, so a Printing print with a start this close is that job. Compared
    /// in memory: the candidates are the printer's open prints, and SQLite can't compare
    /// <see cref="DateTimeOffset"/>.
    /// </summary>
    private async Task<OpenPrint?> SameJobAsync(long userId, long callerBridgeId, long printerId, DateTime started, string? fileName, CancellationToken ct)
    {
        var candidates = await context.Prints.AsNoTracking()
            .Where(p => p.PrinterId == printerId
                && p.CreatedById == userId
                && p.Status == PrintStatus.Printing
                && p.Connection != null
                && p.Connection.BridgeId != null
                && p.Connection.BridgeId != callerBridgeId)
            .Select(p => new { p.Id, p.StartDate, p.FileName, p.ExternalSource, p.ExternalId })
            .ToListAsync(ct);
        var name = BaseName(fileName);
        return candidates
            .Where(p => p.StartDate is { } start && (start.UtcDateTime - started).Duration() <= SameJobTolerance)
            .Where(p => name is null || BaseName(p.FileName) is not { } other || string.Equals(name, other, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => (p.StartDate!.Value.UtcDateTime - started).Duration())
            .Select(p => new OpenPrint(p.Id, p.ExternalSource, p.ExternalId))
            .FirstOrDefault();
    }

    private static string? BaseName(string? fileName) =>
        string.IsNullOrWhiteSpace(fileName) ? null : fileName.Replace('\\', '/').Split('/')[^1].Trim();

    /// <summary>
    /// Relinks the open print to the bound bridge's job and connection, if nothing changed it
    /// since it was read. False when it did (finished, or another request took it over), or when
    /// the bridge's pair is already a print: then the create runs as usual (and replays that one).
    /// </summary>
    private async Task<bool> TakeOverAsync(long userId, OpenPrint open, AddPrintDTO print, CancellationToken ct)
    {
        var source = print.ExternalSource is { } s ? ExternalPrintIds.NormalizeSource(s) : open.ExternalSource;
        var externalId = print.ExternalId is { } i ? ExternalPrintIds.NormalizeId(i) : open.ExternalId;
        Guid? connectionId = null;
        if (!string.IsNullOrWhiteSpace(print.ConnectionInstanceId))
        {
            var instanceId = print.ConnectionInstanceId.Trim();
            connectionId = await context.Connections
                .Where(c => c.UserId == userId && c.InstanceId == instanceId)
                .Select(c => (Guid?)c.Id)
                .FirstOrDefaultAsync(ct);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        int updated;
        try
        {
            updated = await context.Prints
                .Where(p => p.Id == open.Id
                    && p.Status == PrintStatus.Printing
                    && p.ExternalSource == open.ExternalSource
                    && p.ExternalId == open.ExternalId)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(p => p.ExternalSource, source)
                    .SetProperty(p => p.ExternalId, externalId)
                    .SetProperty(p => p.ConnectionId, p => connectionId ?? p.ConnectionId)
                    .SetProperty(p => p.UpdatedById, userId)
                    .SetProperty(p => p.UpdatedDate, now), ct);
        }
        catch (System.Data.Common.DbException)
        {
            // The pair's unique index: a racing create of the same job won. Creating replays it.
            return false;
        }

        if (updated > 0)
        {
            cacheVersions.InvalidateUserCache(userId);
        }

        return updated > 0;
    }
}
