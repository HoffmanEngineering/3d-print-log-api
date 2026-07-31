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
    /// The Printers tab. Printer ownership is Printer.UserId — Printer is not a TimestampEntity
    /// and has no CreatedById — and maintenance is scoped through its printer, so a maintenance
    /// row can never reach a user who does not own the machine it belongs to.
    /// </summary>
    public sealed class PrinterAnalyticsService : IPrinterAnalyticsService
    {
        public const int MaxMaintenanceEvents = 500;

        /// <summary>
        /// Whether the maintenance-totals read would materialize too much. Extracted as a pure
        /// predicate so the boundary is testable without seeding twenty thousand rows — a guard
        /// that can only be exercised by data nobody will seed is a guard nobody verifies.
        /// Inclusive: exactly MaxSeriesRows is allowed.
        /// </summary>
        public static bool ShouldSkipMaintenanceTotals(int rowCount) =>
            rowCount > AnalyticsService.MaxSeriesRows;

        private readonly PrintLogContext _context;

        public PrinterAnalyticsService(PrintLogContext context) => _context = context;

        public async Task<PrintersResponse> GetPrinters(long userId, AnalyticsFilter filter, CancellationToken ct)
        {
            filter.TryResolveTimeZone(out var zone);
            zone ??= TimeZoneInfo.Utc;
            var granularity = filter.ResolveGranularity();

            var scoped = AnalyticsQueryScope.Scope(
                _context.Prints.AsNoTracking(), userId, filter, filter.FromDate, filter.ToDate);

            var coverage = new CoverageBuilder("printers");

            var owned = _context.Printers.AsNoTracking().Where(p => p.UserId == userId);
            if (filter.PrinterIds.Count > 0)
                owned = owned.Where(p => filter.PrinterIds.Contains(p.Id));

            var printers = await owned
                .Select(p => new { p.Id, p.Name, p.Make, p.Model })
                .ToListAsync(ct);
            coverage.Total = printers.Count;

            // Per printer, in SQL. The material sum is an inlined copy of MaterialMgExpr because
            // g.Sum takes a Func, not an Expression (PrintMetrics.cs:31-38).
            var stats = await scoped
                .GroupBy(p => p.PrinterId)
                .Select(g => new
                {
                    PrinterId = g.Key,
                    PrintCount = g.Count(),
                    Success = g.Count(p => p.Status == Print.PrintStatus.Success),
                    Resolved = g.Count(p =>
                        p.Status == Print.PrintStatus.Success ||
                        p.Status == Print.PrintStatus.PartialSuccess ||
                        p.Status == Print.PrintStatus.Failed ||
                        p.Status == Print.PrintStatus.Cancelled),
                    DurationSeconds = g.Sum(p =>
                        (long)(p.PrintTimeInSeconds.HasValue && p.PrintTimeInSeconds > 0 ? p.PrintTimeInSeconds.Value
                        : p.EstimatedPrintTimeInSeconds.HasValue && p.EstimatedPrintTimeInSeconds > 0 ? p.EstimatedPrintTimeInSeconds.Value
                        : 0)),
                    MaterialMg = g.Sum(p =>
                        (long)p.FilamentUsage.Sum(pf =>
                            pf.AmountMg.HasValue && pf.AmountMg > 0 ? pf.AmountMg.Value
                            : pf.EstimatedAmountMg.HasValue && pf.EstimatedAmountMg > 0 ? pf.EstimatedAmountMg.Value
                            : 0)
                        + (p.FilamentUsageMg.HasValue && p.FilamentUsageMg > 0 ? p.FilamentUsageMg.Value
                           : p.EstimatedFilamentUsageMg.HasValue && p.EstimatedFilamentUsageMg > 0 ? p.EstimatedFilamentUsageMg.Value
                           : 0)),
                })
                .ToDictionaryAsync(x => x.PrinterId, ct);

            // Intervals for utilization, plus the per-bucket time series: both need per-print
            // (start, duration), so they share one bounded read.
            var datedCount = await scoped.CountAsync(p => p.StartDate != null, ct);
            var seriesTruncated = datedCount > AnalyticsService.MaxSeriesRows;

            var intervals = seriesTruncated
                ? new List<(long PrinterId, DateTimeOffset Start, int Duration, int Count)>()
                : (await scoped
                    .Where(p => p.StartDate != null)
                    .GroupBy(p => new
                    {
                        p.PrinterId,
                        p.StartDate,
                        Duration = p.PrintTimeInSeconds.HasValue && p.PrintTimeInSeconds > 0
                            ? p.PrintTimeInSeconds.Value
                            : p.EstimatedPrintTimeInSeconds.HasValue && p.EstimatedPrintTimeInSeconds > 0
                                ? p.EstimatedPrintTimeInSeconds.Value
                                : 0,
                    })
                    .Select(g => new { g.Key.PrinterId, g.Key.StartDate, g.Key.Duration, Count = g.Count() })
                    .ToListAsync(ct))
                    // Element names spelled out: `x.StartDate!.Value` infers no name, which would
                    // silently drop `Start` from the tuple type the rest of this method reads.
                    .Select(x => (
                        PrinterId: x.PrinterId,
                        Start: x.StartDate!.Value,
                        Duration: x.Duration,
                        Count: x.Count))
                    .ToList();

            if (seriesTruncated) coverage.Exclude(ExclusionReason.RowCapExceeded, datedCount);
            coverage.Exclude(ExclusionReason.DurationMissing,
                intervals.Where(i => i.Duration <= 0).Sum(i => i.Count));

            var windowFrom = filter.FromDate
                ?? (intervals.Count > 0 ? intervals.Min(i => i.Start) : DateTimeOffset.UtcNow);
            var windowTo = filter.ToDate ?? DateTimeOffset.UtcNow;
            var windowSeconds = Math.Max(0, (windowTo - windowFrom).TotalSeconds);

            var maintenance = await LoadMaintenance(userId, filter, zone, coverage, ct);

            // Money comes from its OWN uncapped read. Summing the capped event list would make a
            // user past 500 entries under-report their spend with nothing on screen to say so.
            var maintenanceByPrinter = await LoadMaintenanceTotals(userId, filter, coverage, ct);

            var costProjection = await AnalyticsCostProjection.Project(_context, userId, scoped, ct);
            if (costProjection.RowCapExceeded)
                coverage.Exclude(ExclusionReason.RowCapExceeded, costProjection.PrintCount);
            else
                foreach (var (reason, count) in AnalyticsCostProjection.CountExclusions(costProjection.Prints))
                    coverage.Exclude(reason, count);

            var costByPrinter = costProjection.Prints
                .GroupBy(p => p.PrinterId)
                .ToDictionary(
                    g => g.Key,
                    g => g.Any(p => p.Total.HasValue) ? g.Sum(p => p.Total ?? 0m) : (decimal?)null);

            var rows = new List<PrinterRow>(printers.Count);
            foreach (var printer in printers)
            {
                stats.TryGetValue(printer.Id, out var s);
                var printCount = s?.PrintCount ?? 0;
                var seconds = s?.DurationSeconds ?? 0;

                double? utilization = null;
                if (printCount > 0 && windowSeconds > 0 && !seriesTruncated)
                {
                    var union = IntervalUnion.UnionSeconds(
                        intervals
                            .Where(i => i.PrinterId == printer.Id && i.Duration > 0)
                            .Select(i => (i.Start, i.Start.AddSeconds(i.Duration))),
                        windowFrom, windowTo);
                    utilization = Math.Min(100.0, 100.0 * union / windowSeconds);
                }

                maintenanceByPrinter.TryGetValue(printer.Id, out var maintenanceCost);
                var printHours = seconds / 3600.0;

                rows.Add(new PrinterRow(
                    PrinterId: printer.Id,
                    Name: string.IsNullOrWhiteSpace(printer.Name)
                        ? $"{printer.Make} {printer.Model}".Trim()
                        : printer.Name,
                    IsIdle: printCount == 0,
                    PrintCount: printCount,
                    SuccessRatePercent: s is null || s.Resolved == 0 ? null : 100.0 * s.Success / s.Resolved,
                    PrintTimeSeconds: seconds,
                    MaterialMg: s?.MaterialMg ?? 0,
                    AvgDurationSeconds: printCount == 0 ? null : (double)seconds / printCount,
                    Cost: costByPrinter.TryGetValue(printer.Id, out var cost) ? cost : null,
                    MaintenanceCost: maintenanceCost,
                    UtilizationPercent: utilization,
                    // Cost of ownership per hour actually printed. Undefined with no print hours:
                    // dividing by zero would report an infinite cost for an idle machine.
                    CostPerPrintHour: maintenanceCost.HasValue && printHours > 0
                        ? (double)maintenanceCost.Value / printHours
                        : null));
            }

            rows = rows.OrderByDescending(r => r.PrintCount).ThenBy(r => r.PrinterId).ToList();
            coverage.Counted = rows.Count(r => !r.IsIdle);

            // Fleet utilization AVERAGES per-printer figures and never sums them, and idle
            // printers are excluded so a dormant machine does not drag the fleet toward zero.
            var active = rows.Where(r => !r.IsIdle && r.UtilizationPercent.HasValue).ToList();
            var fleet = new Metric(
                active.Count == 0 ? null : active.Average(r => r.UtilizationPercent!.Value),
                null,
                new CoverageBuilder("printers") { Total = rows.Count, Counted = active.Count }.Build());

            var series = BuildSeries(intervals, filter, zone, granularity, windowFrom, windowTo);

            return new PrintersResponse(
                filter.FromDate, filter.ToDate, filter.TimeZone, granularity.ToString(),
                costProjection.Inputs.UserCurrency,
                rows, series, fleet, maintenance, coverage.Build());
        }

        private static IReadOnlyList<PrinterSeriesBucket> BuildSeries(
            IReadOnlyList<(long PrinterId, DateTimeOffset Start, int Duration, int Count)> intervals,
            AnalyticsFilter filter, TimeZoneInfo zone, AnalyticsGranularity granularity,
            DateTimeOffset windowFrom, DateTimeOffset windowTo)
        {
            if (windowTo <= windowFrom) return Array.Empty<PrinterSeriesBucket>();

            var buckets = TimeBucketer.BuildBuckets(windowFrom, windowTo, zone, granularity, DayOfWeek.Sunday);
            var accumulator = buckets.ToDictionary(b => b.Index, _ => new Dictionary<string, long>());

            foreach (var interval in intervals)
            {
                if (interval.Duration <= 0) continue;
                var index = TimeBucketer.IndexOf(buckets, interval.Start.ToUniversalTime());
                if (index < 0) continue;

                var key = interval.PrinterId.ToString(CultureInfo.InvariantCulture);
                var slot = accumulator[buckets[index].Index];
                slot[key] = (slot.TryGetValue(key, out var n) ? n : 0) + (long)interval.Duration * interval.Count;
            }

            return buckets
                .Select(b => new PrinterSeriesBucket(
                    b.Index, b.LocalStart, (IReadOnlyDictionary<string, long>)accumulator[b.Index]))
                .ToList();
        }

        /// <summary>
        /// Scoped through the printer, so ownership is enforced by the join rather than by trust.
        /// Shared by both maintenance reads so the money and the event list can never be filtered
        /// differently.
        /// </summary>
        private IQueryable<PrinterMaintenance> MaintenanceQuery(long userId, AnalyticsFilter filter)
        {
            var query = _context.PrinterMaintenance.AsNoTracking()
                .Where(m => m.Printer.UserId == userId && m.Done);

            if (filter.PrinterIds.Count > 0)
                query = query.Where(m => filter.PrinterIds.Contains(m.PrinterId));
            if (filter.HasRange)
                query = query.Where(m => m.Date >= filter.FromDate.Value && m.Date < filter.ToDate.Value);

            return query;
        }

        /// <summary>
        /// Per-printer maintenance spend over EVERY matching row — no cap. Two columns only, so
        /// the read stays cheap even for a heavy maintenance log. A pure SQL SUM is impossible:
        /// PriceValue is free text and must go through ParseInvariant.
        /// </summary>
        private async Task<Dictionary<long, decimal?>> LoadMaintenanceTotals(
            long userId, AnalyticsFilter filter, CoverageBuilder coverage, CancellationToken ct)
        {
            var query = MaintenanceQuery(userId, filter);

            // Uncapped relative to the 500-event DISPLAY limit, but still bounded: "we removed
            // one cap" is not a licence to materialize without limit (spec §6.4). Two narrow
            // columns make the ceiling generous, and exceeding it is reported rather than
            // silently truncating the money.
            var total = await query.CountAsync(ct);
            if (ShouldSkipMaintenanceTotals(total))
            {
                coverage.Exclude(ExclusionReason.RowCapExceeded, total);
                return new Dictionary<long, decimal?>();
            }

            var rows = await query
                .Select(m => new { m.PrinterId, m.PriceValue })
                .ToListAsync(ct);

            // Entered but unreadable is a different fact from never entered, and the Costs tab
            // already reports it this way.
            coverage.Exclude(
                ExclusionReason.PriceMissing,
                rows.Count(r => !string.IsNullOrWhiteSpace(r.PriceValue)
                    && PrintCostCalculator.ParseInvariant(r.PriceValue) is null));

            return rows
                .Select(r => new { r.PrinterId, Cost = PrintCostCalculator.ParseInvariant(r.PriceValue) })
                .GroupBy(r => r.PrinterId)
                .ToDictionary(
                    g => g.Key,
                    g => g.Any(r => r.Cost.HasValue) ? g.Sum(r => r.Cost ?? 0m) : (decimal?)null);
        }

        private async Task<IReadOnlyList<MaintenanceEvent>> LoadMaintenance(
            long userId, AnalyticsFilter filter, TimeZoneInfo zone, CoverageBuilder coverage, CancellationToken ct)
        {
            var query = MaintenanceQuery(userId, filter);

            var total = await query.CountAsync(ct);
            if (total > MaxMaintenanceEvents)
                coverage.Exclude(ExclusionReason.RowCapExceeded, total - MaxMaintenanceEvents);

            var rows = await query
                .OrderByDescending(m => m.Date).ThenBy(m => m.Id)
                .Take(MaxMaintenanceEvents)
                .Select(m => new { m.Id, m.PrinterId, m.Date, m.Category, m.Description, m.PriceValue })
                .ToListAsync(ct);

            return rows
                .Select(m => new MaintenanceEvent(
                    m.Id.ToString(),
                    m.PrinterId,
                    DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(m.Date, zone).DateTime),
                    m.Category,
                    m.Description,
                    // ParseInvariant, not decimal.Parse: invariant culture, finite and
                    // non-negative are already settled rules, and PriceValue is a free-text
                    // string column that users can and do fill with anything.
                    PrintCostCalculator.ParseInvariant(m.PriceValue)))
                .ToList();
        }
    }
}
