using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Services;

/// <summary>
/// Serialises the move of a print to a finished status (#199), for both paths that make it:
/// <see cref="PrintEventService.Finished"/> and <see cref="PrintService.CompletePrint"/>.
/// <see cref="Print"/> has no concurrency token, so two overlapping finishes would otherwise both
/// read the print as unfinished, both merge their usage onto it and both notify.
/// </summary>
public static class PrintFinishTransition
{
    /// <summary>
    /// Saves the context's pending changes in one transaction with a conditional UPDATE that moves
    /// <paramref name="print"/> to its (already assigned) finished <see cref="Print.Status"/> only
    /// while the stored row is still unfinished. Returns false, writing nothing, when another
    /// request finished it first; the caller's tracked entities are then stale and it should clear
    /// the tracker and replay against what that request stored.
    /// </summary>
    /// <remarks>
    /// The claim and the save share a transaction because a claim committed on its own would let
    /// the loser re-read the print as finished but without the winner's usage rows, and add its
    /// own beside them. Here the loser's UPDATE waits on the winner's row lock until the winner's
    /// rows are committed, and a failed save rolls the claim back with it. Changes are accepted
    /// only after the commit, so the retrying execution strategy can replay the whole unit.
    /// </remarks>
    public static async Task<bool> SaveIfStillUnfinished(PrintLogContext context, Print print, long userId)
    {
        var status = print.Status;
        var strategy = context.Database.CreateExecutionStrategy();
        var claimed = await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await context.Database.BeginTransactionAsync();
            var now = DateTime.UtcNow;
            var updated = await context.Prints
                .Where(p => p.Id == print.Id
                    && p.Status != PrintStatus.Success
                    && p.Status != PrintStatus.Cancelled
                    && p.Status != PrintStatus.Failed
                    && p.Status != PrintStatus.PartialSuccess)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(p => p.Status, status)
                    .SetProperty(p => p.UpdatedById, userId)
                    // ExecuteUpdate bypasses the context's SaveChanges timestamping.
                    .SetProperty(p => p.UpdatedDate, now));
            if (updated == 0)
            {
                return false;
            }

            // SaveChanges writes the tracked print's current values, so its status is the finished
            // one the claim just set, never the stale one the print was read with.
            await context.SaveChangesAsync(acceptAllChangesOnSuccess: false);
            await tx.CommitAsync();
            return true;
        });

        if (claimed)
        {
            context.ChangeTracker.AcceptAllChanges();
        }
        return claimed;
    }
}
