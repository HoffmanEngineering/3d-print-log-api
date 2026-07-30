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

        private readonly PrintLogContext _context;

        public AnalyticsService(PrintLogContext context) => _context = context;

        public async Task<OverviewResponse> GetOverview(long userId, AnalyticsFilter filter, CancellationToken ct)
        {
            filter.TryResolveTimeZone(out var zone);
            zone ??= TimeZoneInfo.Utc;
            var granularity = filter.ResolveGranularity();

            var current = await Aggregate(userId, filter, filter.FromDate, filter.ToDate, zone, granularity, ct);

            AggregateResult previous = null;
            if (filter.ComparePrevious && filter.HasRange)
            {
                var (pFrom, pTo) = TimeBucketer.PreviousWindow(filter.FromDate.Value, filter.ToDate.Value);
                previous = await Aggregate(userId, filter, pFrom, pTo, zone, granularity, ct);
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
            IReadOnlyList<string> CostExclusions,
            string Currency,
            Dictionary<string, int> StatusCounts,
            IReadOnlyList<SeriesBucket> Series,
            OverviewHighlights Highlights);

        private async Task<AggregateResult> Aggregate(
            long userId, AnalyticsFilter filter,
            DateTimeOffset? from, DateTimeOffset? to,
            TimeZoneInfo zone, AnalyticsGranularity granularity, CancellationToken ct)
        {
            // Tenant scoping is applied first and never relaxed. Unowned filter ids simply
            // match nothing, which is why an unowned printer id yields zeros rather than an error.
            var owned = _context.Prints.AsNoTracking().Where(p => p.CreatedById == userId);

            var hasRange = from.HasValue && to.HasValue;
            var scoped = hasRange
                ? owned.Where(p => p.StartDate >= from.Value && p.StartDate < to.Value) // half-open
                : owned;

            if (filter.PrinterIds.Count > 0)
                scoped = scoped.Where(p => filter.PrinterIds.Contains(p.PrinterId));
            if (filter.ProjectIds.Count > 0)
                scoped = scoped.Where(p => p.ProjectId.HasValue && filter.ProjectIds.Contains(p.ProjectId.Value));
            if (filter.Statuses.Count > 0)
                scoped = scoped.Where(p => filter.Statuses.Contains(p.Status));
            if (filter.FilamentIds.Count > 0)
                scoped = scoped.Where(p => p.FilamentUsage.Any(pf =>
                    pf.FilamentId.HasValue && filter.FilamentIds.Contains(pf.FilamentId.Value)));

            var printCount = await scoped.CountAsync(ct);
            var undatedCount = hasRange ? 0 : await scoped.CountAsync(p => p.StartDate == null, ct);

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

            var series = await BuildSeries(scoped, from, to, zone, granularity, ct);
            var (cost, costExclusions, currency) = await ComputeCost(userId, scoped, ct);
            var highlights = await BuildHighlights(scoped, ct);

            return new AggregateResult(printCount, undatedCount, durationSeconds, durationEstimated,
                materialMg, materialEstimated, cost, costExclusions, currency, statusCounts, series, highlights);
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
        private static async Task<IReadOnlyList<SeriesBucket>> BuildSeries(
            IQueryable<Print> scoped, DateTimeOffset? from, DateTimeOffset? to,
            TimeZoneInfo zone, AnalyticsGranularity granularity, CancellationToken ct)
        {
            var dated = scoped.Where(p => p.StartDate != null);

            var windowFrom = from ?? await dated.MinAsync(p => p.StartDate, ct) ?? DateTimeOffset.UtcNow;
            var windowTo = to ?? DateTimeOffset.UtcNow;
            if (windowTo <= windowFrom) return Array.Empty<SeriesBucket>();

            var buckets = TimeBucketer.BuildBuckets(windowFrom, windowTo, zone, granularity, DayOfWeek.Sunday);
            if (buckets.Count == 0) return Array.Empty<SeriesBucket>();

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

            return buckets
                .Select(b => new SeriesBucket(b.Index, b.LocalStart, accumulator[b.Index]))
                .ToList();
        }

        private async Task<(decimal? Cost, IReadOnlyList<string> Exclusions, string Currency)> ComputeCost(
            long userId, IQueryable<Print> scoped, CancellationToken ct)
        {
            var settings = await _context.UserSettings.AsNoTracking()
                .Where(s => s.UserId == userId)
                .Select(s => new { s.UserSettingTypeId, s.Value })
                .ToListAsync(ct);

            string Setting(int id) => settings.FirstOrDefault(s => s.UserSettingTypeId == id)?.Value;

            var inputs = new CostInputs(
                UserCurrency: Setting(5),            // Currency_Name
                DefaultFilamentPrice: Setting(8),    // Filaments_DefaultPrice
                KwhRate: Setting(12),                // Electricity_KwhRate
                DefaultWattageW: Setting(13));       // Electricity_DefaultWattageW

            // Cap on the rows that would actually be materialized: one per filament usage row,
            // plus one per print for the printer/electricity term. Counting prints alone would
            // let a multi-material library blow several times past the limit.
            var filamentRows = await scoped.SelectMany(p => p.FilamentUsage).CountAsync(ct);
            var printRows = await scoped.CountAsync(ct);
            if (filamentRows + printRows > MaxCostRows)
                return (null, new[] { ExclusionReason.RowCapExceeded }, inputs.UserCurrency);

            var projected = await scoped
                .Select(p => new
                {
                    DurationSeconds = p.PrintTimeInSeconds.HasValue && p.PrintTimeInSeconds > 0
                        ? p.PrintTimeInSeconds.Value
                        : p.EstimatedPrintTimeInSeconds.HasValue && p.EstimatedPrintTimeInSeconds > 0
                            ? p.EstimatedPrintTimeInSeconds.Value
                            : 0,
                    p.Printer.WattageW,
                    Rows = p.FilamentUsage.Where(pf => pf.Filament != null).Select(pf => new
                    {
                        pf.Filament.PurchasePriceValue,
                        pf.Filament.PurchasePriceCurrency,
                        pf.Filament.InitialNominalWeightMg,
                        pf.Filament.MaterialDensityGramPerCubicCm,
                        pf.Filament.DiameterMm,
                        Source = (int)pf.Source,
                        AmountMg = (double?)pf.AmountMg,
                        pf.LengthInM,
                        pf.VolumeMl,
                        EstimatedSource = (int)pf.EstimatedSource,
                        EstimatedAmountMg = (double?)pf.EstimatedAmountMg,
                        pf.EstimatedLengthInM,
                        pf.EstimatedVolumeMl,
                    }).ToList(),
                })
                .ToListAsync(ct);

            decimal? total = null;
            var exclusions = new List<string>();

            foreach (var p in projected)
            {
                var rows = p.Rows.Select(r => new FilamentCostRow(
                    r.PurchasePriceValue, r.PurchasePriceCurrency, r.InitialNominalWeightMg,
                    r.MaterialDensityGramPerCubicCm, r.DiameterMm,
                    r.Source, r.AmountMg, r.LengthInM, r.VolumeMl,
                    r.EstimatedSource, r.EstimatedAmountMg, r.EstimatedLengthInM, r.EstimatedVolumeMl));

                var filament = PrintCostCalculator.FilamentCost(rows, inputs);
                var electricity = PrintCostCalculator.ElectricityCost(p.DurationSeconds, p.WattageW, inputs);

                if (filament.Amount.HasValue) total = (total ?? 0m) + filament.Amount.Value;
                if (electricity.Amount.HasValue) total = (total ?? 0m) + electricity.Amount.Value;

                exclusions.AddRange(filament.ExclusionReasons);
                exclusions.AddRange(electricity.ExclusionReasons);
            }

            return (total, exclusions.Distinct().ToList(), inputs.UserCurrency);
        }

        private static async Task<OverviewHighlights> BuildHighlights(IQueryable<Print> scoped, CancellationToken ct)
        {
            // Spec §5: ranked by print count, tie-broken by DURATION then MATERIAL MASS, then id
            // as a final deterministic backstop. Both tie-breakers must be projected, or the
            // ordering silently degrades to "lowest id wins", which is not the specified rule.
            // The sums are inlined rather than using PrintMetrics.*Expr because g.Sum takes a
            // Func, not an Expression (PrintMetrics.cs:31-38).
            var topPrinter = await scoped
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
                        p.FilamentUsage.Sum(pf =>
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
                .SelectMany(p => p.FilamentUsage)
                .Where(pf => pf.Filament != null)
                .GroupBy(pf => new { pf.FilamentId, pf.Filament.DisplayName, pf.Filament.MaterialType })
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
                // Priciest print needs per-print costing, which is capped; Phase 4 (Costs tab) owns it.
                null);
        }

        private static OverviewTiles BuildTiles(AggregateResult c, AggregateResult p)
        {
            Coverage Cov(string population, int counted, int total, int undated, params (string Reason, int Count)[] ex)
            {
                var b = new CoverageBuilder(population) { Counted = counted, Total = total, UndatedCount = undated };
                foreach (var (reason, count) in ex) b.Exclude(reason, count);
                return b.Build();
            }

            double? SuccessRate(AggregateResult r)
            {
                if (r is null) return null;
                var d = r.StatusCounts["Success"] + r.StatusCounts["PartialSuccess"]
                        + r.StatusCounts["Failed"] + r.StatusCounts["Cancelled"];
                return d == 0 ? null : 100.0 * r.StatusCounts["Success"] / d;
            }

            double? Avg(AggregateResult r) =>
                r is null || r.PrintCount == 0 ? null : (double)r.DurationSeconds / r.PrintCount;

            return new OverviewTiles(
                PrintCount: new Metric(c.PrintCount, p?.PrintCount, Cov("prints", c.PrintCount, c.PrintCount, c.UndatedCount)),
                SuccessRatePercent: new Metric(SuccessRate(c), SuccessRate(p), Cov("prints", c.PrintCount, c.PrintCount, c.UndatedCount)),
                FilamentGrams: new Metric(c.MaterialMg / 1000.0, p is null ? null : p.MaterialMg / 1000.0,
                    Cov("prints", c.PrintCount, c.PrintCount, c.UndatedCount,
                        (ExclusionReason.MaterialEstimated, c.MaterialEstimatedCount))),
                PrintTimeSeconds: new Metric(c.DurationSeconds, p?.DurationSeconds,
                    Cov("prints", c.PrintCount, c.PrintCount, c.UndatedCount,
                        (ExclusionReason.DurationEstimated, c.DurationEstimatedCount))),
                TotalCost: new MoneyMetric(c.Cost, p?.Cost, c.Currency,
                    Cov("prints", c.PrintCount, c.PrintCount, c.UndatedCount,
                        c.CostExclusions.Select(r => (r, 1)).ToArray())),
                AvgPrintTimeSeconds: new Metric(Avg(c), Avg(p), Cov("prints", c.PrintCount, c.PrintCount, c.UndatedCount)));
        }
    }
}
