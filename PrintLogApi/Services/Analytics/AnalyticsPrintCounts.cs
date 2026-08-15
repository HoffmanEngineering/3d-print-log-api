#nullable enable

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
        /// The aggregate itself, separate from executing it, so a test can assert how it
        /// TRANSLATES without needing a database to run it against. The integration suite runs on
        /// SQLite and production is SQL Server, so "it translates on the provider we ship" is a
        /// claim the correctness tests structurally cannot make.
        ///
        /// GroupBy over a constant is the translatable spelling of "aggregate the whole set".
        /// </summary>
        public static IQueryable<ScopedPrintCounts> Query(IQueryable<Print> scoped) =>
            scoped
                .GroupBy(_ => 1)
                .Select(g => new ScopedPrintCounts(
                    g.Count(),
                    g.Count(p => p.StartDate == null),
                    g.Count(p => p.StartDate != null),
                    g.Min(p => p.StartDate)));

        /// <summary>
        /// An empty set produces NO row rather than a row of zeros, which is what the Empty
        /// fallback is for — not a defensive null check, the documented shape of the result.
        /// </summary>
        public static async Task<ScopedPrintCounts> Load(
            IQueryable<Print> scoped, CancellationToken ct) =>
            await Query(scoped).FirstOrDefaultAsync(ct) ?? ScopedPrintCounts.Empty;
    }
}
