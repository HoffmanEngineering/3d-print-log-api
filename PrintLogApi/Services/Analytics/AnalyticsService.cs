#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Analytics;

namespace PrintLogApi.Services.Analytics
{
    /// <summary>
    /// Two-stage aggregation. Stage 1 is SQL and groups to the smallest useful unit; stage 2 is
    /// bounded in-memory work over stage-1 rows only. Nothing materializes the user's print list.
    /// </summary>
    public sealed class AnalyticsService : IAnalyticsService
    {
        /// <summary>
        /// The cost tile is the only metric needing per-filament-row projection, so it is capped on
        /// the ROWS it would materialize, not on the print count: a print with four spools yields
        /// four rows, so a cap counting prints can silently materialize several times its own limit.
        /// Above this, costing is skipped and RowCapExceeded is reported.
        /// </summary>
        public const int MaxCostRows = 20000;

        /// <summary>
        /// The series groups by exact instant, so its row count is bounded by the number of
        /// dated prints in range — fine for a month, but an all-time or 20-year range on a
        /// large library would stream tens of thousands of rows back to be bucketed in memory.
        /// Above this the series is omitted and reported as RowCapExceeded rather than silently
        /// returning an empty chart or quietly doing the unbounded work.
        /// </summary>
        public const int MaxSeriesRows = 20000;

        private readonly PrintLogContext _context;

        public AnalyticsService(PrintLogContext context) => _context = context;

        public async Task<OverviewResponse> GetOverview(long userId, AnalyticsFilter filter, CancellationToken ct)
        {
            filter.TryResolveTimeZone(out var zone);
            zone ??= TimeZoneInfo.Utc;
            var granularity = filter.ResolveGranularity();

            var current = await Aggregate(userId, filter, filter.FromDate, filter.ToDate, zone, granularity, ct);

            // PreviousWindow, not TimeBucketer.PreviousWindow: the latter subtracts a UTC span,
            // which lands an hour off local midnight whenever the range crosses a DST boundary.
            // The other five tabs use this helper, and one screen must not show the same delta
            // computed two ways.
            AggregateResult? previous = null;
            var previousFilter = PreviousWindow.For(filter);
            if (previousFilter is not null)
            {
                previous = await Aggregate(
                    userId, previousFilter, previousFilter.FromDate, previousFilter.ToDate,
                    zone, granularity, ct);
            }

            return new OverviewResponse(
                filter.FromDate,
                filter.ToDate,
                filter.TimeZone,
                granularity.ToString(),
                BuildTiles(current, previous),
                current.StatusCounts.Select(kv => new StatusCount(kv.Key, kv.Value)).OrderBy(s => s.Status).ToList(),
                current.Series,
                current.Highlights);
        }

        private sealed record AggregateResult(
            int PrintCount,
            int UndatedCount,
            long DurationSeconds,
            int DurationEstimatedCount,
            long MaterialMg,
            int MaterialEstimatedCount,
            decimal? Cost,
            IReadOnlyDictionary<string, int> CostExclusions,
            string Currency,
            Dictionary<string, int> StatusCounts,
            IReadOnlyList<SeriesBucket> Series,
            bool SeriesTruncated,
            OverviewHighlights Highlights);

        private async Task<AggregateResult> Aggregate(
            long userId, AnalyticsFilter filter,
            DateTimeOffset? from, DateTimeOffset? to,
            TimeZoneInfo zone, AnalyticsGranularity granularity, CancellationToken ct)
        {
            var hasRange = from.HasValue && to.HasValue;
            var scoped = AnalyticsQueryScope.Scope(
                _context.Prints.AsNoTracking(), userId, filter, from, to);

            // One aggregate for the plain counts. The four metric sums below stay as separate
            // top-level calls on purpose: g.Sum takes a Func rather than an Expression, so
            // folding them in here would mean inlining copies of the shared PrintMetrics
            // expressions and letting the overview drift from every other tab that uses them.
            var counts = await AnalyticsPrintCounts.Load(scoped, ct);
            var printCount = counts.Total;
            var undatedCount = hasRange ? 0 : counts.Undated;

            // Top-level sums so the shared expressions translate. Never inside a GroupBy.
            var durationSeconds = await scoped.SumAsync(PrintMetrics.DurationSecondsExpr, ct);
            var durationEstimated = await scoped.CountAsync(PrintMetrics.DurationIsEstimatedExpr, ct);
            var materialMg = await scoped.SumAsync(PrintMetrics.MaterialMgExpr, ct);
            var materialEstimated = await scoped.CountAsync(PrintMetrics.MaterialIsEstimatedExpr, ct);

            var statusCounts = await scoped
                .GroupBy(p => p.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Status.ToString(), x => x.Count, ct);

            // A missing key reads as "unknown", not "none".
            foreach (var name in Enum.GetNames<Print.PrintStatus>()) statusCounts.TryAdd(name, 0);

            var (series, seriesTruncated) = await BuildSeries(scoped, counts, from, to, zone, granularity, ct);
            var (cost, costExclusions, currency, priciest) = await ComputeCost(userId, scoped, ct);
            var highlights = await BuildHighlights(scoped, userId, priciest, ct);

            return new AggregateResult(printCount, undatedCount, durationSeconds, durationEstimated,
                materialMg, materialEstimated, cost, costExclusions, currency, statusCounts,
                series, seriesTruncated, highlights);
        }

        /// <summary>
        /// Stage 1 groups by the raw StartDate instant and status.
        ///
        /// The design called for grouping on { Date, Hour, Minute } to shrink the group count, but
        /// SQLite cannot translate a GroupBy over those DateTimeOffset components (it stores the
        /// value as text) and throws rather than degrading. Grouping on the instant itself is
        /// trivially translatable on every provider and is still bounded by the print count — the
        /// worst case, every print at a distinct timestamp, is the same bound the component
        /// grouping had. Client evaluation was NOT an option: it would materialize every print.
        ///
        /// Full-instant grain also removes the reason minute grain was needed in the first place:
        /// 45-minute zones (Kathmandu, Chatham) place local midnight inside a UTC hour, so any
        /// coarser grain risks misattributing prints in a boundary period.
        /// </summary>
        private static async Task<(IReadOnlyList<SeriesBucket> Series, bool Truncated)> BuildSeries(
            IQueryable<Print> scoped, ScopedPrintCounts counts,
            DateTimeOffset? from, DateTimeOffset? to,
            TimeZoneInfo zone, AnalyticsGranularity granularity, CancellationToken ct)
        {
            var dated = scoped.Where(p => p.StartDate != null);

            // The window start and the row cap both come from the caller's single aggregate.
            // MIN ignores NULLs in SQL, so the earliest start over the whole scoped set is the
            // earliest DATED start — this is the same number the separate MinAsync returned.
            var windowFrom = from ?? counts.EarliestStart ?? DateTimeOffset.UtcNow;
            var windowTo = to ?? DateTimeOffset.UtcNow;
            if (windowTo <= windowFrom) return (Array.Empty<SeriesBucket>(), false);

            var buckets = TimeBucketer.BuildBuckets(windowFrom, windowTo, zone, granularity, DayOfWeek.Sunday);
            if (buckets.Count == 0) return (Array.Empty<SeriesBucket>(), false);

            // Bound the work before doing it. Grouping is by exact instant, so the returned row
            // count is at most the dated print count. Reporting truncation beats either silently
            // returning an empty chart or running an unbounded query.
            if (counts.Dated > MaxSeriesRows)
            {
                return (Array.Empty<SeriesBucket>(), true);
            }

            var groups = await dated
                .GroupBy(p => new { p.StartDate, p.Status })
                .Select(g => new
                {
                    g.Key.StartDate,
                    g.Key.Status,
                    Count = g.Count(),
                })
                .ToListAsync(ct);

            var accumulator = buckets
                .ToDictionary(b => b.Index, _ => Enum.GetNames<Print.PrintStatus>().ToDictionary(n => n, _ => 0));

            foreach (var g in groups)
            {
                if (g.StartDate is null) continue;
                var index = TimeBucketer.IndexOf(buckets, g.StartDate.Value.ToUniversalTime());
                if (index < 0) continue;
                accumulator[buckets[index].Index][g.Status.ToString()] += g.Count;
            }

            return (
                buckets
                    .Select(b => new SeriesBucket(b.Index, b.LocalStart, accumulator[b.Index]))
                    .ToList(),
                false);
        }

        private async Task<(decimal? Cost, IReadOnlyDictionary<string, int> Exclusions, string Currency, HighlightRef? Priciest)> ComputeCost(
            long userId, IQueryable<Print> scoped, CancellationToken ct)
        {
            var projection = await AnalyticsCostProjection.Project(_context, userId, scoped, ct);

            if (projection.RowCapExceeded)
                return (
                    null,
                    new Dictionary<string, int> { [ExclusionReason.RowCapExceeded] = projection.PrintCount },
                    projection.Inputs.UserCurrency,
                    // No projection means no priciest print. Guessing one from a second, cheaper
                    // pass would put a figure on screen the cost tile has already declined to show.
                    null);

            var total = projection.Prints.Any(p => p.Total.HasValue)
                ? projection.Prints.Sum(p => p.Total ?? 0m)
                : (decimal?)null;

            // Priciest print, from the SAME projection the cost tile uses — computing it from a
            // second pass is how the tile and the highlight would come to disagree.
            var priciest = projection.Prints
                .Where(p => p.Total.HasValue)
                .OrderByDescending(p => p.Total!.Value).ThenBy(p => p.PrintId)
                .Select(p => new HighlightRef(
                    p.PrintId.ToString(CultureInfo.InvariantCulture), p.Title, (double)p.Total!.Value, "cost"))
                .FirstOrDefault();

            return (total, AnalyticsCostProjection.CountExclusions(projection.Prints), projection.Inputs.UserCurrency, priciest);
        }

        private static async Task<OverviewHighlights> BuildHighlights(
            IQueryable<Print> scoped, long userId, HighlightRef? priciest, CancellationToken ct)
        {
            // Spec §5: ranked by print count, tie-broken by DURATION then MATERIAL MASS, then id
            // as a final deterministic backstop. Both tie-breakers must be projected, or the
            // ordering silently degrades to "lowest id wins", which is not the specified rule.
            // The sums are inlined rather than using PrintMetrics.*Expr because g.Sum takes a
            // Func, not an Expression (PrintMetrics.cs:31-38).
            // Owner-scoped before the group: this projection reads printer NAME, make and model,
            // so an unowned reference here would surface another user's machine on the tile.
            var topPrinter = await scoped
                .Where(p => p.Printer.UserId == userId)
                .GroupBy(p => new { p.PrinterId, p.Printer.Name, p.Printer.Make, p.Printer.Model })
                .Select(g => new
                {
                    g.Key.PrinterId,
                    g.Key.Name,
                    g.Key.Make,
                    g.Key.Model,
                    Count = g.Count(),
                    DurationSeconds = g.Sum(p =>
                        p.PrintTimeInSeconds.HasValue && p.PrintTimeInSeconds > 0 ? p.PrintTimeInSeconds.Value
                        : p.EstimatedPrintTimeInSeconds.HasValue && p.EstimatedPrintTimeInSeconds > 0 ? p.EstimatedPrintTimeInSeconds.Value
                        : 0),
                    MaterialMg = g.Sum(p =>
                        p.FilamentUsage!.Sum(pf =>
                            pf.AmountMg.HasValue && pf.AmountMg > 0 ? pf.AmountMg.Value
                            : pf.EstimatedAmountMg.HasValue && pf.EstimatedAmountMg > 0 ? pf.EstimatedAmountMg.Value
                            : 0)
                        + (p.FilamentUsageMg.HasValue && p.FilamentUsageMg > 0 ? p.FilamentUsageMg.Value
                           : p.EstimatedFilamentUsageMg.HasValue && p.EstimatedFilamentUsageMg > 0 ? p.EstimatedFilamentUsageMg.Value
                           : 0)),
                })
                .OrderByDescending(x => x.Count)
                .ThenByDescending(x => x.DurationSeconds)
                .ThenByDescending(x => x.MaterialMg)
                .ThenBy(x => x.PrinterId)
                .FirstOrDefaultAsync(ct);

            // Duration is not attributable to an individual spool on a multi-material print, so
            // the material ranking tie-breaks on mass alone, then id. Same rule, minus the term
            // that has no meaning at this grain.
            var topMaterial = await scoped
                .SelectMany(p => p.FilamentUsage!)
                // Reads DisplayName and MaterialType, so ownership is required, not just
                // existence. Unlike the mass sums, a null Filament genuinely has nothing to
                // rank here, so `linked AND owned` is the right predicate in this one place.
                .Where(pf => pf.Filament != null && pf.Filament.CreatedById == userId)
                .GroupBy(pf => new { pf.FilamentId, pf.Filament!.DisplayName, pf.Filament.MaterialType })
                .Select(g => new
                {
                    g.Key.FilamentId,
                    g.Key.DisplayName,
                    g.Key.MaterialType,
                    Count = g.Count(),
                    MaterialMg = g.Sum(pf =>
                        pf.AmountMg.HasValue && pf.AmountMg > 0 ? pf.AmountMg.Value
                        : pf.EstimatedAmountMg.HasValue && pf.EstimatedAmountMg > 0 ? pf.EstimatedAmountMg.Value
                        : 0),
                })
                .OrderByDescending(x => x.Count)
                .ThenByDescending(x => x.MaterialMg)
                .ThenBy(x => x.FilamentId)
                .FirstOrDefaultAsync(ct);

            var longest = await scoped
                .Select(p => new
                {
                    p.Id,
                    p.Title,
                    Seconds = p.PrintTimeInSeconds.HasValue && p.PrintTimeInSeconds > 0
                        ? p.PrintTimeInSeconds.Value
                        : p.EstimatedPrintTimeInSeconds.HasValue && p.EstimatedPrintTimeInSeconds > 0
                            ? p.EstimatedPrintTimeInSeconds.Value
                            : 0,
                })
                .OrderByDescending(x => x.Seconds).ThenBy(x => x.Id)
                .FirstOrDefaultAsync(ct);

            return new OverviewHighlights(
                topPrinter is null ? null : new HighlightRef(
                    topPrinter.PrinterId.ToString(CultureInfo.InvariantCulture),
                    string.IsNullOrWhiteSpace(topPrinter.Name)
                        ? $"{topPrinter.Make} {topPrinter.Model}".Trim()
                        : topPrinter.Name,
                    topPrinter.Count, "prints"),
                topMaterial is null ? null : new HighlightRef(
                    topMaterial.FilamentId?.ToString(),
                    string.IsNullOrWhiteSpace(topMaterial.DisplayName) ? topMaterial.MaterialType : topMaterial.DisplayName,
                    topMaterial.Count, "prints"),
                longest is null ? null : new HighlightRef(
                    longest.Id.ToString(CultureInfo.InvariantCulture), longest.Title, longest.Seconds, "seconds"),
                // Computed in ComputeCost, from the same projection the cost tile uses, and null
                // whenever that projection could not price anything.
                priciest);
        }

        private static OverviewTiles BuildTiles(AggregateResult c, AggregateResult? p)
        {
            Coverage Cov(string population, int counted, int total, int undated, params (string Reason, int Count)[] ex)
            {
                var b = new CoverageBuilder(population) { Counted = counted, Total = total, UndatedCount = undated };
                foreach (var (reason, count) in ex) b.Exclude(reason, count);
                return b.Build();
            }

            double? SuccessRate(AggregateResult? r)
            {
                if (r is null) return null;
                var d = r.StatusCounts["Success"] + r.StatusCounts["PartialSuccess"]
                        + r.StatusCounts["Failed"] + r.StatusCounts["Cancelled"];
                return d == 0 ? null : 100.0 * r.StatusCounts["Success"] / d;
            }

            double? Avg(AggregateResult? r) =>
                r is null || r.PrintCount == 0 ? null : (double)r.DurationSeconds / r.PrintCount;

            // A dropped series is reported on the print-count tile, which is the one the chart
            // is drawn from — the UI renders this as its coverage note, so an empty chart comes
            // with a reason instead of looking like "you have no prints".
            var printCountCoverage = Cov(
                "prints", c.PrintCount, c.PrintCount, c.UndatedCount,
                (ExclusionReason.RowCapExceeded, c.SeriesTruncated ? c.PrintCount : 0));

            return new OverviewTiles(
                PrintCount: new Metric(c.PrintCount, p?.PrintCount, printCountCoverage),
                SuccessRatePercent: new Metric(SuccessRate(c), SuccessRate(p), Cov("prints", c.PrintCount, c.PrintCount, c.UndatedCount)),
                FilamentGrams: new Metric(c.MaterialMg / 1000.0, p is null ? null : p.MaterialMg / 1000.0,
                    Cov("prints", c.PrintCount, c.PrintCount, c.UndatedCount,
                        (ExclusionReason.MaterialEstimated, c.MaterialEstimatedCount))),
                PrintTimeSeconds: new Metric(c.DurationSeconds, p?.DurationSeconds,
                    Cov("prints", c.PrintCount, c.PrintCount, c.UndatedCount,
                        (ExclusionReason.DurationEstimated, c.DurationEstimatedCount))),
                TotalCost: new MoneyMetric(c.Cost, p?.Cost, c.Currency,
                    Cov("prints", c.PrintCount, c.PrintCount, c.UndatedCount,
                        c.CostExclusions.Select(kv => (kv.Key, kv.Value)).ToArray())),
                AvgPrintTimeSeconds: new Metric(Avg(c), Avg(p), Cov("prints", c.PrintCount, c.PrintCount, c.UndatedCount)));
        }
    }
}
