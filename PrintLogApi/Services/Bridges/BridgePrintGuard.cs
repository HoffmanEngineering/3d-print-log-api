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

    /// <summary>The job is already a print: answer with <see cref="BridgePrintDecision.PrintId"/> instead.</summary>
    UsePrint,
}

/// <param name="Shared">The caller reported a job for a printer another bridge is bound to (counted on the caller).</param>
public sealed record BridgePrintDecision(BridgePrintVerdict Verdict, long? PrintId = null, bool HandedOver = false, bool Shared = false)
{
    public static readonly BridgePrintDecision Accepted = new(BridgePrintVerdict.Accept);
}

public interface IBridgePrintGuard
{
    /// <summary>
    /// Decides a print create (<c>POST /api/Prints</c>) sent with bridge <paramref name="grantId"/>'s
    /// device token. Writes the shared-job counter, or the hand-off, itself.
    /// </summary>
    Task<BridgePrintDecision> CheckCreateAsync(long userId, long grantId, AddPrintDTO print, CancellationToken ct);

    /// <summary>Decides a claim (<c>POST /api/Prints/{id}/claim</c>): accepted, or answered with the bound bridge's print of the same job. Never handed over.</summary>
    Task<BridgePrintDecision> CheckClaimAsync(long userId, long grantId, long printId, ClaimPrintDto claim, CancellationToken ct);
}

/// <summary>
/// The cross-bridge safety net (printlog-bridge#103, story 15a). Connect already refuses a second
/// watcher, but bridges that identify one printer by different fields, or a printer in one
/// bridge's config file and another's desired list, can still both watch it, and each logs every
/// job under its own instance id.
/// <para>
/// <b>A duplicate print is acceptable; a lost print, or two jobs merged into one, is not.</b> So
/// nothing is refused: printlog-bridge drops a 400/403 for good, and no state the API can see
/// proves the bound bridge is logging the printer (an adapter that can't reach it still
/// heartbeats without an error). A job is matched to an existing print only when it is
/// evidently the same job: both file names present and equal, starts within the kind's
/// <see cref="SameJobTolerance"/>, and neither print over before the other began.
/// </para>
/// <list type="bullet">
/// <item>Only new prints are judged: a replay of an existing pair, and every write to a print that
/// exists (complete, patch, images), go through.</item>
/// <item>Only a bridge is judged: a device token whose grant checked in as a bridge. API keys and
/// other devices name no bridge.</item>
/// <item>Another bridge's job for the printer, started since it was bound (<see cref="BridgePrinter.MovedAt"/>,
/// else its <see cref="BridgePrinter.CreatedDate"/>): counted on that bridge
/// (<see cref="Bridge.SharedJobCount"/>), and answered with the bound bridge's print of the same
/// job when there is one, else logged.</item>
/// <item>The old bridge's job from before the move: its outbox may send it late. It gets another
/// bridge's print of the same job, in any status, that started no later than the move plus
/// <see cref="MoveSlack"/>; else it is logged. Not counted.</item>
/// <item>Hand-off: the old bridge stops watching within a minute of a move and never sees the
/// running job end; the new bridge finds it running at first look. Its create for a job that
/// started no later than the move plus <see cref="MoveSlack"/> takes over another bridge's
/// Printing print of the same job that started before the move, and no more than
/// <see cref="HandOffWindow"/> before it. A job that started after the move never takes a print
/// over, so a reprint is never merged.</item>
/// </list>
/// </summary>
public sealed class BridgePrintGuard(PrintLogContext context, TimeProvider clock, ICacheVersionService cacheVersions) : IBridgePrintGuard
{
    /// <summary>
    /// How far apart two bridges' start times for one job may be. Both read the printer's own
    /// figure: Moonraker's job history start time, PrusaLink's printing time (now less it, so
    /// seconds apart plus clock skew), Bambu Lab's <c>gcode_start_time</c>. A Bambu Lab printer
    /// without that field gives each bridge its own first look, so Bambu Lab gets 10 minutes;
    /// every other kind 2.
    /// </summary>
    public static TimeSpan SameJobTolerance(string kind) =>
        string.Equals(kind, "bambu", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(2);

    /// <summary>How long after the move a job may have started and still be the one running at the move (the move's own request and the bridges' clocks).</summary>
    public static readonly TimeSpan MoveSlack = TimeSpan.FromMinutes(2);

    /// <summary>How long before the move an open print may have started and still be the running job, not one left Printing.</summary>
    public static readonly TimeSpan HandOffWindow = TimeSpan.FromHours(48);

    /// <summary>How long a shared job is remembered, so sending it again isn't counted again.</summary>
    public static readonly TimeSpan SharedJobRetention = TimeSpan.FromDays(30);

    private sealed record Binding(long BridgeId, string Kind, DateTime BoundSince);

    /// <summary>The job as the caller reports it.</summary>
    private sealed record Job(long PrinterId, DateTime Started, DateTime? Ended, string? FileName);

    private sealed record Candidate(long Id, DateTimeOffset? StartDate, int? PrintTimeInSeconds, PrintStatus Status, string? FileName, string? ExternalSource, string? ExternalId);

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
        var job = new Job(print.PrinterId, started, print.PrintTimeInSeconds is { } seconds ? started.AddSeconds(seconds) : null, print.FileName);
        var tolerance = SameJobTolerance(binding.Kind);
        if (binding.BridgeId == grantId)
        {
            if (started <= binding.BoundSince + MoveSlack)
            {
                var open = Match(await CandidatesAsync(userId, job.PrinterId, c => c.BridgeId != grantId, ct), job, tolerance,
                    c => c.Status == PrintStatus.Printing
                        && c.StartDate!.Value.UtcDateTime <= binding.BoundSince
                        && c.StartDate.Value.UtcDateTime >= binding.BoundSince - HandOffWindow);
                if (open is not null && await TakeOverAsync(userId, grantId, job.PrinterId, open, print, ct))
                {
                    return new BridgePrintDecision(BridgePrintVerdict.UsePrint, open.Id, HandedOver: true);
                }
            }

            return BridgePrintDecision.Accepted;
        }

        if (started < binding.BoundSince)
        {
            // The old bridge's job from before the move.
            var logged = Match(await CandidatesAsync(userId, job.PrinterId, c => c.BridgeId != grantId, ct), job, tolerance,
                c => c.StartDate!.Value.UtcDateTime <= binding.BoundSince + MoveSlack);
            return logged is null ? BridgePrintDecision.Accepted : new BridgePrintDecision(BridgePrintVerdict.UsePrint, logged.Id);
        }

        return await SharedAsync(userId, grantId, binding, job, pair, now, ct);
    }

    public async Task<BridgePrintDecision> CheckClaimAsync(long userId, long grantId, long printId, ClaimPrintDto claim, CancellationToken ct)
    {
        var target = await context.Prints.AsNoTracking()
            .Where(p => p.Id == printId && p.CreatedById == userId)
            .Select(p => new { p.PrinterId, p.StartDate, p.FileName, p.ExternalSource, p.ExternalId })
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
        var started = (claim.StartDate ?? target.StartDate)?.UtcDateTime ?? now;
        if (started < binding.BoundSince)
        {
            return BridgePrintDecision.Accepted;
        }

        return await SharedAsync(userId, grantId, binding, new Job(target.PrinterId, started, null, target.FileName), pair, now, ct);
    }

    /// <summary>
    /// Another bridge's job for a printer bound to <paramref name="binding"/>'s bridge: counted on
    /// the caller, and answered with the bound bridge's print of the same job, if any.
    /// </summary>
    private async Task<BridgePrintDecision> SharedAsync(long userId, long grantId, Binding binding, Job job, ExternalPair? pair, DateTime now, CancellationToken ct)
    {
        var newJob = await RememberSharedJobAsync(grantId, pair, now, ct);

        // One statement, so concurrent jobs can't lose an increment. No cache version: nothing
        // cached reads bridges.
        var increment = newJob ? 1 : 0;
        await context.Bridges
            .Where(b => b.Id == grantId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.SharedJobCount, b => b.SharedJobCount + increment)
                .SetProperty(b => b.LastSharedJobAt, now)
                .SetProperty(b => b.LastSharedJobPrinterId, job.PrinterId), ct);

        var same = Match(await CandidatesAsync(userId, job.PrinterId, c => c.BridgeId == binding.BridgeId, ct), job, SameJobTolerance(binding.Kind), _ => true);
        return same is null
            ? new BridgePrintDecision(BridgePrintVerdict.Accept, Shared: true)
            : new BridgePrintDecision(BridgePrintVerdict.UsePrint, same.Id, Shared: true);
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
            .Select(p => new Binding(p.BridgeId, p.Kind, p.MovedAt ?? p.CreatedDate))
            .SingleOrDefaultAsync(ct);
        if (binding is null || !await context.Bridges.AnyAsync(b => b.Id == grantId && b.Grant.UserId == userId, ct))
        {
            return null;
        }

        return binding;
    }

    /// <summary>
    /// Records the shared job by its pair; false when it was already recorded (the bridge sends a
    /// job at its start, again at its end, and may claim it). A job with no pair can't be told
    /// apart, so it counts each time. The unique index decides a race.
    /// </summary>
    private async Task<bool> RememberSharedJobAsync(long grantId, ExternalPair? pair, DateTime now, CancellationToken ct)
    {
        if (pair is null)
        {
            return true;
        }

        var forgetBefore = now - SharedJobRetention;
        await context.BridgeSharedJobs.Where(j => j.BridgeId == grantId && j.ReportedAt < forgetBefore).ExecuteDeleteAsync(ct);
        if (await context.BridgeSharedJobs.AnyAsync(j => j.BridgeId == grantId && j.ExternalSource == pair.Source && j.ExternalId == pair.Id, ct))
        {
            return false;
        }

        var row = new BridgeSharedJob { BridgeId = grantId, ExternalSource = pair.Source, ExternalId = pair.Id, ReportedAt = now };
        context.BridgeSharedJobs.Add(row);
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

    /// <summary>The printer's recent prints logged through a bridge connection that <paramref name="bridge"/> accepts, newest first.</summary>
    private async Task<List<Candidate>> CandidatesAsync(long userId, long printerId, System.Linq.Expressions.Expression<Func<Connection, bool>> bridge, CancellationToken ct)
    {
        var connections = context.Connections.Where(c => c.UserId == userId && c.BridgeId != null).Where(bridge).Select(c => c.Id);
        return await context.Prints.AsNoTracking()
            .Where(p => p.PrinterId == printerId && p.CreatedById == userId && p.ConnectionId != null && connections.Contains(p.ConnectionId.Value))
            .OrderByDescending(p => p.Id)
            .Take(50)
            .Select(p => new Candidate(p.Id, p.StartDate, p.PrintTimeInSeconds, p.Status, p.FileName, p.ExternalSource, p.ExternalId))
            .ToListAsync(ct);
    }

    /// <summary>
    /// The candidate that is evidently <paramref name="job"/>, closest start first, or null: both
    /// file names present and equal, starts within <paramref name="tolerance"/>, and neither over
    /// before the other began (a failed first attempt and its reprint share a file and nearly a
    /// start). Compared in memory: SQLite can't compare <see cref="DateTimeOffset"/>.
    /// </summary>
    private static Candidate? Match(List<Candidate> candidates, Job job, TimeSpan tolerance, Func<Candidate, bool> also)
    {
        if (BaseName(job.FileName) is not { } name)
        {
            return null;
        }

        return candidates
            .Where(c => c.StartDate is { } start
                && string.Equals(BaseName(c.FileName), name, StringComparison.OrdinalIgnoreCase)
                && (start.UtcDateTime - job.Started).Duration() <= tolerance
                && (job.Ended is not { } ended || start.UtcDateTime <= ended)
                && !EndedBefore(c, job.Started)
                && also(c))
            .OrderBy(c => (c.StartDate!.Value.UtcDateTime - job.Started).Duration())
            .FirstOrDefault();
    }

    /// <summary>Whether a finished candidate ended before <paramref name="started"/>.</summary>
    private static bool EndedBefore(Candidate c, DateTime started) =>
        c.Status != PrintStatus.Printing && c.Status != PrintStatus.Pending
        && c.StartDate is { } start && c.PrintTimeInSeconds is { } seconds
        && start.UtcDateTime.AddSeconds(seconds) < started;

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
    private async Task<bool> TakeOverAsync(long userId, long boundBridgeId, long printerId, Candidate open, AddPrintDTO print, CancellationToken ct)
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
