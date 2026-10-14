using Microsoft.EntityFrameworkCore;
using PrintLogApi.Controllers;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Services.Spoolman;

/// <summary>What became of one <c>/use</c> report. The metric label is its lower snake case.</summary>
public enum SpoolmanUseOutcome
{
    /// <summary>Added to the open notifier print's accumulation for the spool.</summary>
    Recorded,

    /// <summary>The connection is not linked to a printer.</summary>
    NoPrinter,

    /// <summary>The printer has a live printlog-bridge, which records the job's usage itself.</summary>
    Bridge,

    /// <summary>The printer has no Printing print logged by the notifier.</summary>
    NoPrint,
}

/// <summary>
/// Stores Moonraker's <c>PUT /spool/{id}/use</c> reports (#171) as one running total per
/// (print, spool) on the printer's open notifier print, for the notifier's finish to turn into
/// the print's usage.
/// </summary>
public interface ISpoolmanUsageRecorder
{
    /// <param name="userId">The connection's user.</param>
    /// <param name="printerId">The connection's printer, from its claim; null when unlinked.</param>
    /// <param name="filamentId">The reported spool, already resolved to the user's filament.</param>
    /// <param name="millimetres">The reported length; negative is Moonraker taking usage back.</param>
    Task<SpoolmanUseOutcome> RecordAsync(long userId, long? printerId, Guid filamentId, double millimetres, CancellationToken ct);
}

public class SpoolmanUsageRecorder(
    PrintLogContext context,
    IPrintEventService printEvents,
    IPrinterService printers,
    ICacheVersionService cacheVersionService,
    TimeProvider clock) : ISpoolmanUsageRecorder
{
    public async Task<SpoolmanUseOutcome> RecordAsync(long userId, long? printerId, Guid filamentId, double millimetres, CancellationToken ct)
    {
        if (printerId is not { } printer)
        {
            return SpoolmanUseOutcome.NoPrinter;
        }

        // Read-only: a report is not a notifier event, so it is not counted as a dropped one.
        if (await printEvents.IsBridgeLive(userId, printer))
        {
            return SpoolmanUseOutcome.Bridge;
        }

        // Newest by Id: StartDate is a DateTimeOffset, which SQLite cannot order by.
        var printId = await context.Prints
            .Where(p => p.PrinterId == printer
                && p.CreatedById == userId
                && p.Status == PrintStatus.Printing
                && p.ExternalSource == MoonrakerController.NotifierSource)
            .OrderByDescending(p => p.Id)
            .Select(p => (long?)p.Id)
            .FirstOrDefaultAsync(ct);
        if (printId is not { } print)
        {
            return SpoolmanUseOutcome.NoPrint;
        }

        var now = clock.GetUtcNow();

        // The common case, every few seconds for the length of the print: one statement, so two
        // reports cannot lose each other's millimetres. It changes nothing a cached summary shows,
        // so it does not bump the user's cache version.
        if (await IncrementAsync(print, filamentId, millimetres, now, ct) > 0)
        {
            return SpoolmanUseOutcome.Recorded;
        }

        context.SpoolmanUsages.Add(new SpoolmanUsage
        {
            PrintId = print,
            FilamentId = filamentId,
            LengthMm = millimetres,
            UpdatedAt = now,
        });
        var loaded = await LoadIntoSingleSlotAsync(userId, printer, filamentId, ct);

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (UniqueConstraintViolation.IsUniqueViolation(ex))
        {
            // Another first report for the same (print, spool) inserted between our increment and
            // our insert. Its row stands, and so does any slot it loaded; add ours to it.
            context.ChangeTracker.Clear();
            await IncrementAsync(print, filamentId, millimetres, now, ct);
            return SpoolmanUseOutcome.Recorded;
        }

        if (loaded)
        {
            cacheVersionService.InvalidateUserCache(userId);
        }

        return SpoolmanUseOutcome.Recorded;
    }

    private Task<int> IncrementAsync(long printId, Guid filamentId, double millimetres, DateTimeOffset now, CancellationToken ct) =>
        context.SpoolmanUsages
            .Where(u => u.PrintId == printId && u.FilamentId == filamentId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.LengthMm, u => u.LengthMm + millimetres)
                .SetProperty(u => u.UpdatedAt, now), ct);

    /// <summary>
    /// Moonraker reports only its active spool, so on a single-tool printer the spool being used is
    /// the one loaded: stage loading it into slot 0 when that is not what slot 0 holds. Done on a
    /// spool's first report of a print only, never per increment. A multi-slot printer is left
    /// alone, because a report does not say which slot the spool is in.
    /// </summary>
    /// <returns>Whether anything was staged; the caller saves it.</returns>
    private async Task<bool> LoadIntoSingleSlotAsync(long userId, long printerId, Guid filamentId, CancellationToken ct)
    {
        var printer = await context.Printers
            .FirstOrDefaultAsync(p => p.Id == printerId && p.UserId == userId, ct);
        if (printer is null || printer.SlotCount != 1)
        {
            return false;
        }

        // Slot 0, and rows loaded before slots existed, which are slot 0's on a single-tool
        // printer (as PrintEventService.SpoolInSlot reads them).
        var inSlot = await context.PrinterFilament
            .Where(pf => pf.PrinterId == printerId && (pf.Slot == 0 || pf.Slot == null))
            .ToListAsync(ct);
        if (inSlot.Count > 0 && inSlot.All(pf => pf.FilamentId == filamentId))
        {
            return false;
        }

        // LoadSlot displaces slot 0 only; an unslotted row would otherwise stay loaded beside it.
        // Local time, matching LoadSlot and the rest of these columns.
        foreach (var legacy in inSlot.Where(pf => pf.Slot == null && pf.FilamentId != filamentId))
        {
            legacy.UnloadedDateTime = DateTimeOffset.Now;
        }

        await printers.LoadSlot(printer, 0, filamentId, null);
        return true;
    }
}
