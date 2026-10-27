using Microsoft.EntityFrameworkCore;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Services.Bridges;

/// <summary>What the cross-bridge safety net decided for a bridge's new print.</summary>
public enum BridgePrintVerdict
{
    /// <summary>Go ahead as usual.</summary>
    Accept,

    /// <summary>Another bridge is bound to the printer and evidently logging it: refuse (403) and count it.</summary>
    Refuse,

    /// <summary>The job is already logged by another bridge: answer with <see cref="BridgePrintDecision.PrintId"/>.</summary>
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
/// <para>
/// <b>A refusal loses the print for good</b> (printlog-bridge drops a 403 from its outbox), so it
/// needs evidence that the bound bridge is logging the printer itself
/// (<see cref="BoundBridgeIsWatchingAsync"/>): a live heartbeat for the printer without a recent
/// error, no reported credential issue, and every credential the printer can't connect without
/// sealed to the bridge's current key. Without it, the job is logged: a possible duplicate beats a
/// lost print.
/// </para>
/// <list type="bullet">
/// <item>Only new prints are judged: a replay of an existing pair, and every write to a print that
/// exists (complete, patch, images), go through.</item>
/// <item>Only a bridge is judged: a device token whose grant checked in as a bridge. API keys and
/// other devices name no bridge.</item>
/// <item>A job that started before the printer was bound (<see cref="BridgePrinter.MovedAt"/>,
/// else its <see cref="BridgePrinter.CreatedDate"/>) is the old bridge's: accepted, since its
/// outbox may send it late, unless another bridge already logged it (any status), which it then
/// gets back instead of a second print that would never finish.</item>
/// <item>Hand-off: the old bridge stops watching within a minute of a move and never sees the
/// running job end; the new bridge finds it running at first look. Its create, for a job that
/// started within <see cref="HandOffGrace"/> of the move, takes over the old bridge's open print
/// of the same file (both names present), started no later than the move plus
/// <see cref="SameJobTolerance"/>: the one whose start is within that tolerance of its own, else
/// the previous bridge's only open print, started before the move (for adapters that report
/// their first look as the start).</item>
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

    /// <summary>How long a refused job is remembered, so sending it again isn't counted again.</summary>
    public static readonly TimeSpan DroppedJobRetention = TimeSpan.FromDays(30);

    private sealed record Binding(long EntryId, long BridgeId, string Kind, DateTime BoundSince, DateTime? MovedAt, long? MovedFromBridgeId, string? CredentialIssues, string? BridgeFingerprint);

    public async Task<BridgePrintDecision> CheckCreateAsync(long userId, long grantId, AddPrintDTO print, CancellationToken ct)
    {
        if (await BindingAsync(userId, grantId, print.PrinterId, ct) is not { } binding)
        {
            return BridgePrintDecision.Accepted;
        }

        var pair = Pair(print.ExternalSource, print.ExternalId);
        if (pair is { } p && await context.Prints.AnyAsync(x => x.CreatedById == userId && x.ExternalSource == p.Source && x.ExternalId == p.Id, ct))
        {
            return BridgePrintDecision.Accepted;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var started = print.StartDate?.UtcDateTime ?? now;
        if (binding.BridgeId == grantId)
        {
            if (started <= binding.BoundSince + HandOffGrace
                && await HandOffCandidateAsync(userId, binding, print.PrinterId, started, print.FileName, ct) is { } open
                && await TakeOverAsync(userId, binding.BridgeId, print.PrinterId, open, print, ct))
            {
                return new BridgePrintDecision(BridgePrintVerdict.UsePrint, PrintId: open.Id, HandedOver: true);
            }

            return BridgePrintDecision.Accepted;
        }

        if (started < binding.BoundSince)
        {
            return await LoggedElsewhereAsync(userId, grantId, print.PrinterId, started, print.FileName, ct) is { } logged
                ? new BridgePrintDecision(BridgePrintVerdict.UsePrint, PrintId: logged)
                : BridgePrintDecision.Accepted;
        }

        return await RefuseIfWatchedAsync(grantId, print.PrinterId, binding, pair, now, ct);
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
        var pair = Pair(claim.ExternalSource, claim.ExternalId);
        if (pair is { } p
            && string.Equals(target.ExternalSource, p.Source, StringComparison.OrdinalIgnoreCase)
            && string.Equals(target.ExternalId, p.Id, StringComparison.OrdinalIgnoreCase))
        {
            return BridgePrintDecision.Accepted;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if ((claim.StartDate?.UtcDateTime ?? now) < binding.BoundSince)
        {
            return BridgePrintDecision.Accepted;
        }

        return await RefuseIfWatchedAsync(grantId, target.PrinterId, binding, pair, now, ct);
    }

    private sealed record ExternalPair(string Source, string Id);

    private static ExternalPair? Pair(string? source, string? id) =>
        string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(id)
            ? null
            : new ExternalPair(ExternalPrintIds.NormalizeSource(source), ExternalPrintIds.NormalizeId(id));

    /// <summary>The printer's desired entry, when the caller is a bridge of the user's and the printer is the user's. Null otherwise.</summary>
    private async Task<Binding?> BindingAsync(long userId, long grantId, long printerId, CancellationToken ct)
    {
        var binding = await context.BridgePrinters.AsNoTracking()
            .Where(p => p.PrinterId == printerId && p.Printer.UserId == userId)
            .Select(p => new Binding(p.Id, p.BridgeId, p.Kind, p.MovedAt ?? p.CreatedDate, p.MovedAt, p.MovedFromBridgeId, p.CredentialIssues, p.Bridge.PublicKeyFingerprint))
            .SingleOrDefaultAsync(ct);
        if (binding is null || !await context.Bridges.AnyAsync(b => b.Id == grantId && b.Grant.UserId == userId, ct))
        {
            return null;
        }

        return binding;
    }

    /// <summary>
    /// Whether the bound bridge is evidently logging the printer: it can connect (no reported
    /// credential issue, every credential the kind can't connect without sealed to its current key,
    /// none stale) and it does (a heartbeat for the printer within <see cref="Connection.StaleAfter"/>
    /// with no error reported in that window). A bridge that is online but can't watch the printer
    /// is not evidence: refusing then would lose the print.
    /// </summary>
    private async Task<bool> BoundBridgeIsWatchingAsync(Binding binding, long printerId, DateTime now, CancellationToken ct)
    {
        if (binding.CredentialIssues is not null)
        {
            return false;
        }

        var secrets = await context.BridgePrinterSecrets.AsNoTracking()
            .Where(s => s.BridgePrinterId == binding.EntryId)
            .Select(s => new { s.Field, s.KeyFingerprint })
            .ToListAsync(ct);
        if (secrets.Any(s => s.KeyFingerprint != binding.BridgeFingerprint))
        {
            return false;
        }

        var sealedNow = secrets.Select(s => s.Field).ToHashSet(StringComparer.Ordinal);
        if (!SealedCredentials.RequiredGroups(binding.Kind).All(group => group.Any(sealedNow.Contains)))
        {
            return false;
        }

        var liveSince = now - Connection.StaleAfter;
        var connections = await context.Connections.AsNoTracking()
            .Where(c => c.BridgeId == binding.BridgeId && c.PrinterId == printerId && c.LastSeenAt > liveSince)
            .Select(c => new { c.LastError, c.LastErrorAt })
            .ToListAsync(ct);
        return connections.Any(c => c.LastError is null || (c.LastErrorAt is { } at && at <= liveSince));
    }

    private async Task<BridgePrintDecision> RefuseIfWatchedAsync(long grantId, long printerId, Binding binding, ExternalPair? pair, DateTime now, CancellationToken ct)
    {
        if (!await BoundBridgeIsWatchingAsync(binding, printerId, now, ct))
        {
            return BridgePrintDecision.Accepted;
        }

        var newJob = await RememberDroppedJobAsync(grantId, pair, now, ct);

        // One statement, so concurrent refusals can't lose an increment. No cache version: nothing
        // cached reads bridges.
        var increment = newJob ? 1 : 0;
        await context.Bridges
            .Where(b => b.Id == grantId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.DroppedEventCount, b => b.DroppedEventCount + increment)
                .SetProperty(b => b.LastDroppedEventAt, now)
                .SetProperty(b => b.LastDroppedPrinterId, printerId), ct);
        return new BridgePrintDecision(BridgePrintVerdict.Refuse, BoundBridgeId: binding.BridgeId);
    }

    /// <summary>
    /// Records the refused job by its pair; false when it was already refused (the bridge sends a
    /// job at its start, again at its end, and may claim it). A job with no pair can't be told
    /// apart, so it counts each time. The unique index decides a race.
    /// </summary>
    private async Task<bool> RememberDroppedJobAsync(long grantId, ExternalPair? pair, DateTime now, CancellationToken ct)
    {
        if (pair is null)
        {
            return true;
        }

        var forgetBefore = now - DroppedJobRetention;
        await context.BridgeDroppedJobs.Where(j => j.BridgeId == grantId && j.DroppedAt < forgetBefore).ExecuteDeleteAsync(ct);
        if (await context.BridgeDroppedJobs.AnyAsync(j => j.BridgeId == grantId && j.ExternalSource == pair.Source && j.ExternalId == pair.Id, ct))
        {
            return false;
        }

        var row = new BridgeDroppedJob { BridgeId = grantId, ExternalSource = pair.Source, ExternalId = pair.Id, DroppedAt = now };
        context.BridgeDroppedJobs.Add(row);
        try
        {
            await context.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (UniqueConstraintViolation.IsUniqueViolation(ex))
        {
            return false;
        }
        finally
        {
            context.Entry(row).State = EntityState.Detached;
        }
    }

    private sealed record OpenPrint(long Id, string? ExternalSource, string? ExternalId);

    /// <summary>
    /// The open print the bound bridge's create is the same job as, or null. File names must both
    /// be present and equal, and the print must have started no later than the move plus
    /// <see cref="SameJobTolerance"/>, so a later job of the old bridge's is never merged. Then
    /// the print whose start is closest, within the tolerance; else the previous bridge's only
    /// open print, started before the move. Compared in memory: the candidates are the printer's
    /// open prints, and SQLite can't compare <see cref="DateTimeOffset"/>.
    /// </summary>
    private async Task<OpenPrint?> HandOffCandidateAsync(long userId, Binding binding, long printerId, DateTime started, string? fileName, CancellationToken ct)
    {
        if (BaseName(fileName) is not { } name)
        {
            return null;
        }

        var open = await context.Prints.AsNoTracking()
            .Where(p => p.PrinterId == printerId
                && p.CreatedById == userId
                && p.Status == PrintStatus.Printing
                && p.Connection != null
                && p.Connection.BridgeId != null
                && p.Connection.BridgeId != binding.BridgeId)
            .Select(p => new { p.Id, p.StartDate, p.FileName, p.ExternalSource, p.ExternalId, p.Connection!.BridgeId })
            .ToListAsync(ct);
        var latestStart = binding.BoundSince + SameJobTolerance;
        var sameFile = open
            .Where(p => p.StartDate is { } start && start.UtcDateTime <= latestStart
                && string.Equals(BaseName(p.FileName), name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var byStart = sameFile
            .Where(p => (p.StartDate!.Value.UtcDateTime - started).Duration() <= SameJobTolerance)
            .OrderBy(p => (p.StartDate!.Value.UtcDateTime - started).Duration())
            .FirstOrDefault();
        if (byStart is not null)
        {
            return new OpenPrint(byStart.Id, byStart.ExternalSource, byStart.ExternalId);
        }

        // An adapter that can't read the job's start reports its first look. The previous bridge
        // can have had one job running at the move; if it left exactly one open, that is it.
        if (binding is { MovedAt: { } movedAt, MovedFromBridgeId: { } previous }
            && open.Where(p => p.BridgeId == previous).ToList() is [var only]
            && only.StartDate is { } onlyStart && onlyStart.UtcDateTime <= movedAt
            && sameFile.Any(p => p.Id == only.Id))
        {
            return new OpenPrint(only.Id, only.ExternalSource, only.ExternalId);
        }

        return null;
    }

    /// <summary>
    /// For the old bridge's late create of a job from before the move: the print another bridge
    /// logged for it, in any status (the new bridge may have finished it already), or null. Same
    /// printer, start within <see cref="SameJobTolerance"/>, both file names present and equal.
    /// </summary>
    private async Task<long?> LoggedElsewhereAsync(long userId, long callerBridgeId, long printerId, DateTime started, string? fileName, CancellationToken ct)
    {
        if (BaseName(fileName) is not { } name)
        {
            return null;
        }

        var candidates = await context.Prints.AsNoTracking()
            .Where(p => p.PrinterId == printerId
                && p.CreatedById == userId
                && p.Connection != null
                && p.Connection.BridgeId != null
                && p.Connection.BridgeId != callerBridgeId)
            .OrderByDescending(p => p.Id)
            .Take(50)
            .Select(p => new { p.Id, p.StartDate, p.FileName })
            .ToListAsync(ct);
        return candidates
            .Where(p => p.StartDate is { } start && (start.UtcDateTime - started).Duration() <= SameJobTolerance
                && string.Equals(BaseName(p.FileName), name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => (p.StartDate!.Value.UtcDateTime - started).Duration())
            .Select(p => (long?)p.Id)
            .FirstOrDefault();
    }

    private static string? BaseName(string? fileName) =>
        string.IsNullOrWhiteSpace(fileName) ? null : fileName.Replace('\\', '/').Split('/')[^1].Trim() is { Length: > 0 } name ? name : null;

    /// <summary>
    /// Relinks the open print to the bound bridge's job and connection, if nothing changed it
    /// since it was read. The connection is the one the create names, else the bound bridge's
    /// latest for the printer (a create can come before its connection registered), else none:
    /// never left on the old bridge's. False when the print changed (finished, or another request
    /// took it over), or when the bridge's pair is already a print: then the create runs as usual
    /// (and replays that one).
    /// </summary>
    private async Task<bool> TakeOverAsync(long userId, long boundBridgeId, long printerId, OpenPrint open, AddPrintDTO print, CancellationToken ct)
    {
        var source = print.ExternalSource is { } s ? ExternalPrintIds.NormalizeSource(s) : open.ExternalSource;
        var externalId = print.ExternalId is { } i ? ExternalPrintIds.NormalizeId(i) : open.ExternalId;
        Guid? connectionId = null;
        if (!string.IsNullOrWhiteSpace(print.ConnectionInstanceId))
        {
            var instanceId = print.ConnectionInstanceId.Trim();
            connectionId = await context.Connections
                .Where(c => c.UserId == userId && c.InstanceId == instanceId && c.BridgeId == boundBridgeId)
                .Select(c => (Guid?)c.Id)
                .FirstOrDefaultAsync(ct);
        }

        connectionId ??= await context.Connections
            .Where(c => c.UserId == userId && c.BridgeId == boundBridgeId && c.PrinterId == printerId)
            .OrderByDescending(c => c.LastSeenAt)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);

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
                    .SetProperty(p => p.ConnectionId, connectionId)
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
