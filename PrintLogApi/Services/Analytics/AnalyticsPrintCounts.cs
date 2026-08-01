using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;

namespace PrintLogApi.Services.Analytics
{
    /// <summary>
    /// The counts every analytics endpoint needs about one scoped print set, read in a SINGLE
    /// aggregate query.
    ///
    /// Each tab previously issued these as separate round-trips — a COUNT for coverage, another
    /// for the undated exclusion, another for the series row cap, a MIN for the open-ended window
    /// — all over the identical filtered set, and the cost projection then counted the prints a
    /// fourth time. They are all cheap column predicates, so one grouped aggregate answers every
    /// one of them for the price of the single scan the first COUNT was already paying for.
    /// </summary>
    public sealed record ScopedPrintCounts(
        int Total,
        int Undated,
        int Dated,
        DateTimeOffset? EarliestStart)
    {
        public static readonly ScopedPrintCounts Empty = new(0, 0, 0, null);
    }

    public static class AnalyticsPrintCounts
    {
        /// <summary>
        /// GroupBy over a constant is the translatable spelling of "aggregate the whole set":
        /// it emits the aggregates with no GROUP BY clause on both SQL Server and SQLite. An
        /// empty set produces NO row rather than a row of zeros, which is what the Empty
        /// fallback is for — not a defensive null check, the documented shape of the result.
        /// </summary>
        public static async Task<ScopedPrintCounts> Load(
            IQueryable<Print> scoped, CancellationToken ct)
        {
            var row = await scoped
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    Total = g.Count(),
                    Undated = g.Count(p => p.StartDate == null),
                    Dated = g.Count(p => p.StartDate != null),
                    EarliestStart = g.Min(p => p.StartDate),
                })
                .FirstOrDefaultAsync(ct);

            return row is null
                ? ScopedPrintCounts.Empty
                : new ScopedPrintCounts(row.Total, row.Undated, row.Dated, row.EarliestStart);
        }
    }
}
