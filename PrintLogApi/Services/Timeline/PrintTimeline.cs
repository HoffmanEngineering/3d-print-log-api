using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Services.Timeline;

public enum TimelineInputType
{
    Pause,
    Resume,
}

/// <summary>A pause or resume a connector or webhook reports, at the printer's time.</summary>
public sealed record TimelineInput(TimelineInputType Type, DateTimeOffset OccurredAt, string Key);

/// <summary>Why a print failed, as a connector reported it with <c>/complete</c>.</summary>
public sealed record TimelineFailure(string? Code, string? Message);

/// <summary>
/// Every rule of a print's timeline (api#343). Pauses are a span on a print that stays Printing:
/// <see cref="Print.PausedSince"/> is set while one is open, <see cref="Print.PausedSeconds"/> sums
/// the closed ones. Replays are harmless (keys are unique per print; a resume needs an open pause),
/// and a print keeps at most <see cref="PrintTimelineEvent.MaxPerPrint"/> entries.
/// </summary>
public interface IPrintTimeline
{
    /// <summary>
    /// Applies pauses and resumes, in time order, then saves. Ignores what the rules ignore (see
    /// the spec): a pause on a print that isn't Printing or is already paused, a resume with no open
    /// pause or earlier than it, a key already recorded, a time outside the print's start minus 5
    /// minutes to now plus 5 minutes. Throws <see cref="DoesNotExistException"/> for no such print.
    /// </summary>
    Task ApplyAsync(long printId, IReadOnlyList<TimelineInput> inputs, PrintSource source, CancellationToken ct);
}

public class PrintTimeline(
    PrintLogContext context,
    ICacheVersionService cacheVersionService,
    TelemetryClient telemetry,
    TimeProvider clock,
    ILogger<PrintTimeline> logger) : IPrintTimeline
{
    /// <summary>How far a reported time may sit before the print's start, or after now.</summary>
    public static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    public const string StartedKey = "started";
    public const string FinishedKey = "finished";
    public const string FailureKey = "failure";
    public const string ClaimedKey = "claimed";

    /// <summary>
    /// Stages the Started entry on a print being created in Printing, or moved into it, so it is
    /// saved with the print. <paramref name="at"/> is the print's start, else now.
    /// </summary>
    public static void StageStarted(Print print, DateTimeOffset at)
    {
        print.TimelineEvents ??= new List<PrintTimelineEvent>();
        if (print.TimelineEvents.Any(e => e.Key == StartedKey))
        {
            return;
        }
        print.TimelineEvents.Add(new PrintTimelineEvent
        {
            Print = print,
            Type = PrintTimelineEventType.Started,
            OccurredAt = at,
            Source = print.Source,
            Key = StartedKey,
        });
    }

    public Task ApplyAsync(long printId, IReadOnlyList<TimelineInput> inputs, PrintSource source, CancellationToken ct) =>
        ApplyAsync(printId, inputs, source, retry: true, ct);

    private async Task ApplyAsync(
        long printId, IReadOnlyList<TimelineInput> inputs, PrintSource source, bool retry, CancellationToken ct)
    {
        // Untracked: its pause columns are written only if they are still what was read (Commit).
        var print = await context.Prints.AsNoTracking().FirstOrDefaultAsync(p => p.Id == printId, ct)
            ?? throw new DoesNotExistException();
        var (status, pausedSince, pausedSeconds) = (print.Status, print.PausedSince, print.PausedSeconds);
        var keys = await KeysAsync(context, printId, ct);
        var open = await OpenPauseAsync(context, print, ct);
        var now = clock.GetUtcNow();
        var changed = false;

        foreach (var input in inputs.OrderBy(i => i.OccurredAt))
        {
            var at = input.OccurredAt;
            if (at > now + Skew || (print.StartDate is { } start && at < start - Skew))
            {
                continue;
            }

            if (input.Type == TimelineInputType.Pause)
            {
                if (print.Status != PrintStatus.Printing || print.PausedSince is not null || keys.Contains(input.Key))
                {
                    continue;
                }
                if (keys.Count >= PrintTimelineEvent.MaxPerPrint)
                {
                    CapReached(printId);
                    continue;
                }
                open = new PrintTimelineEvent
                {
                    PrintId = printId,
                    Type = PrintTimelineEventType.Paused,
                    OccurredAt = at,
                    Source = source,
                    Key = input.Key,
                };
                context.PrintTimelineEvents.Add(open);
                keys.Add(input.Key);
                print.PausedSince = at;
                changed = true;
            }
            else if (print.PausedSince is { } since && at >= since)
            {
                ClosePause(print, open, at);
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        bool committed;
        try
        {
            committed = await CommitAsync(print, status, pausedSince, pausedSeconds, ct);
        }
        catch (DbUpdateException ex)
        {
            // A racing delivery of the same pause won the (PrintId, Key) index: it is recorded.
            logger.LogInformation(ex, "A timeline event for print {PrintId} was recorded by another request.", printId);
            context.ChangeTracker.Clear();
            return;
        }
        if (!committed)
        {
            // Another request paused, resumed or finished the print since it was read: apply these
            // again to what it left (once; a key it recorded is skipped then).
            context.ChangeTracker.Clear();
            if (retry)
            {
                await ApplyAsync(printId, inputs, source, retry: false, ct);
            }
            return;
        }
        cacheVersionService.InvalidateUserCache(print.CreatedById);
    }

    /// <summary>
    /// Writes the print's pause columns only if its status and pause columns are still the ones read,
    /// then the timeline rows, in one transaction. False, writing nothing, when they changed.
    /// </summary>
    private async Task<bool> CommitAsync(
        Print print, PrintStatus status, DateTimeOffset? pausedSince, int? pausedSeconds, CancellationToken ct)
    {
        var strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await context.Database.BeginTransactionAsync(ct);
            var updated = await context.Prints
                .Where(p => p.Id == print.Id
                    && p.Status == status
                    && p.PausedSince == pausedSince
                    && p.PausedSeconds == pausedSeconds)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(p => p.PausedSince, print.PausedSince)
                    .SetProperty(p => p.PausedSeconds, print.PausedSeconds), ct);
            if (updated == 0)
            {
                return false;
            }
            await context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        });
    }

    /// <summary>
    /// Stages the Finished entry (and Failure, for a Failed or Cancelled print given one) on a
    /// print tracked by <paramref name="db"/> whose finished status is already assigned, and closes
    /// an open pause at <paramref name="finishedAt"/>. Saves nothing: the caller's finish save
    /// carries it, so a finish that loses its race (<see cref="PrintFinishTransition"/>) drops these
    /// with its other tracked changes. Static, on the caller's context, for that reason. Call it
    /// only for the move from unfinished to finished.
    /// </summary>
    public static async Task StageFinishAsync(
        PrintLogContext db, Print print, DateTimeOffset finishedAt, TimelineFailure? failure, CancellationToken ct)
    {
        var keys = await KeysAsync(db, print.Id, ct);
        AddIfRoom(db, keys, new PrintTimelineEvent
        {
            PrintId = print.Id,
            Type = PrintTimelineEventType.Finished,
            OccurredAt = finishedAt,
            Source = print.Source,
            Code = print.Status.ToString(),
            Key = FinishedKey,
        });

        if (print.Status is PrintStatus.Failed or PrintStatus.Cancelled
            && failure is not null
            && (!string.IsNullOrWhiteSpace(failure.Code) || !string.IsNullOrWhiteSpace(failure.Message)))
        {
            AddIfRoom(db, keys, new PrintTimelineEvent
            {
                PrintId = print.Id,
                Type = PrintTimelineEventType.Failure,
                OccurredAt = finishedAt,
                Source = print.Source,
                Code = Clip(failure.Code, PrintTimelineEvent.MaxCodeLength),
                Message = Clip(failure.Message, PrintTimelineEvent.MaxMessageLength),
                Key = FailureKey,
            });
        }

        if (print.PausedSince is { } since)
        {
            ClosePause(print, await OpenPauseAsync(db, print, ct), finishedAt < since ? since : finishedAt);
        }

        // A pause that committed after this finish read the print has a row its PausedSince
        // doesn't show: the end closes it too, so no pause outlives its print.
        var orphans = await db.PrintTimelineEvents
            .Where(e => e.PrintId == print.Id && e.Type == PrintTimelineEventType.Paused && e.EndedAt == null)
            .ToListAsync(ct);
        foreach (var orphan in orphans)
        {
            orphan.EndedAt = finishedAt < orphan.OccurredAt ? orphan.OccurredAt : finishedAt;
        }
    }

    /// <summary>
    /// Records that a connector claimed the print (once: a later claim finds the key), on
    /// <paramref name="db"/>, and saves. A racing duplicate is ignored.
    /// </summary>
    public static async Task RecordClaimedAsync(
        PrintLogContext db, long printId, PrintSource source, string externalSource, DateTimeOffset at, CancellationToken ct)
    {
        var keys = await KeysAsync(db, printId, ct);
        if (!AddIfRoom(db, keys, new PrintTimelineEvent
        {
            PrintId = printId,
            Type = PrintTimelineEventType.Claimed,
            OccurredAt = at,
            Source = source,
            Code = Clip(externalSource, PrintTimelineEvent.MaxCodeLength),
            Key = ClaimedKey,
        }))
        {
            return;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>Adds the entry unless its key is recorded or the print is at its cap.</summary>
    private static bool AddIfRoom(PrintLogContext db, HashSet<string> keys, PrintTimelineEvent entry)
    {
        if (keys.Contains(entry.Key) || keys.Count >= PrintTimelineEvent.MaxPerPrint)
        {
            return false;
        }
        db.PrintTimelineEvents.Add(entry);
        keys.Add(entry.Key);
        return true;
    }

    /// <summary>Ends the open pause at <paramref name="at"/> (never before it began).</summary>
    private static void ClosePause(Print print, PrintTimelineEvent? open, DateTimeOffset at)
    {
        if (print.PausedSince is not { } since)
        {
            return;
        }
        if (open is not null)
        {
            open.EndedAt = at;
        }
        var seconds = (long)Math.Max(0, (at - since).TotalSeconds);
        print.PausedSeconds = (int)Math.Min(int.MaxValue, (print.PausedSeconds ?? 0) + seconds);
        print.PausedSince = null;
    }

    private static async Task<HashSet<string>> KeysAsync(PrintLogContext db, long printId, CancellationToken ct) =>
        (await db.PrintTimelineEvents
            .Where(e => e.PrintId == printId)
            .Select(e => e.Key)
            .ToListAsync(ct)).ToHashSet();

    private static async Task<PrintTimelineEvent?> OpenPauseAsync(PrintLogContext db, Print print, CancellationToken ct) =>
        print.PausedSince is null
            ? null
            : await db.PrintTimelineEvents
                .Where(e => e.PrintId == print.Id && e.Type == PrintTimelineEventType.Paused && e.EndedAt == null)
                .OrderByDescending(e => e.Id)
                .FirstOrDefaultAsync(ct);

    private void CapReached(long printId) =>
        telemetry.TrackEvent("PrintTimeline_CapReached",
            new Dictionary<string, string> { { "PrintId", printId.ToString(System.Globalization.CultureInfo.InvariantCulture) } });

    private static string? Clip(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim() is var t && t.Length > max ? t[..max] : t;
}
