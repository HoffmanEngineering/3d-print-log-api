using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Mcp;
using PrintLogApi.Models;

namespace PrintLogApi.Services
{
    public sealed class McpStatisticsService : IMcpStatisticsService
    {
        private readonly PrintLogContext _context;

        public McpStatisticsService(PrintLogContext context)
        {
            _context = context;
        }

        public async Task<McpPage<PrinterStatsItem>> GetPrinterStats(
            long userId, DateTimeOffset? from, DateTimeOffset? to, long? printerId,
            int page, int pageSize, CancellationToken ct)
        {
            var prints = _context.Prints.AsNoTracking()
                .Where(p => p.CreatedById == userId && p.Printer.UserId == userId);

            // Omitted range means all-time. Note this excludes undated prints from a ranged query,
            // matching the summary tool's semantics.
            if (from.HasValue && to.HasValue)
            {
                prints = prints.Where(p => p.StartDate >= from.Value && p.StartDate <= to.Value);
            }
            if (printerId.HasValue)
            {
                prints = prints.Where(p => p.PrinterId == printerId.Value);
            }

            // The printer name is joined INTO the grouping key so that ordering and paging run in
            // SQL. The previous version materialized every group and then sorted in memory, which
            // cannot be paged correctly and is unbounded for a user with many printers.
            var grouped = prints
                .GroupBy(p => new { p.PrinterId, p.Printer.Name })
                .Select(g => new
                {
                    g.Key.PrinterId,
                    g.Key.Name,
                    TotalPrints = g.Count(),
                    SuccessfulPrints = g.Count(p => p.Status == Print.PrintStatus.Success),
                    FailedPrints = g.Count(p => p.Status == Print.PrintStatus.Failed),
                    // Inlined rather than PrintMetrics.DurationSecondsExpr: g.Sum takes a Func, not
                    // an Expression, so passing the shared expression here is a compile error.
                    // PrintMetricsTranslationTests pins this copy to the shared rule.
                    TotalPrintTimeSeconds = g.Sum(p =>
                        p.PrintTimeInSeconds.HasValue && p.PrintTimeInSeconds > 0 ? p.PrintTimeInSeconds.Value
                        : p.EstimatedPrintTimeInSeconds.HasValue && p.EstimatedPrintTimeInSeconds > 0 ? p.EstimatedPrintTimeInSeconds.Value
                        : 0),
                    PrintsWithEstimatedDuration = g.Count(p =>
                        !(p.PrintTimeInSeconds.HasValue && p.PrintTimeInSeconds > 0)
                        && p.EstimatedPrintTimeInSeconds.HasValue && p.EstimatedPrintTimeInSeconds > 0),
                })
                .OrderBy(g => g.Name)
                .ThenBy(g => g.PrinterId);

            var totalCount = await grouped.CountAsync(ct);

            var rows = await grouped
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(ct);

            var items = rows
                .Select(g => new PrinterStatsItem(
                    g.PrinterId,
                    g.Name!,
                    g.TotalPrints,
                    g.SuccessfulPrints,
                    g.FailedPrints,
                    McpUnits.SuccessRatePercent(g.SuccessfulPrints, g.TotalPrints),
                    g.TotalPrintTimeSeconds,
                    g.PrintsWithEstimatedDuration))
                .ToList();

            var totalPages = pageSize > 0 ? (int)Math.Ceiling(totalCount / (double)pageSize) : 0;
            return new McpPage<PrinterStatsItem>(items, page, pageSize, totalCount, totalPages);
        }

        // Canonical material usage: the per-filament rows PLUS "other filament" (the scalar
        // Print.FilamentUsageMg — material never attached to a tracked spool). Both terms
        // resolve actual-then-estimate, with zero or negative meaning "not recorded".
        // Every sum here is top-level, so the shared expressions translate. `?? 0` on the
        // duration was the live defect: a never-completed print has a null actual and a real
        // estimate, and reported 0.
        private static async Task<SummaryMetrics> Aggregate(IQueryable<Print> prints, CancellationToken ct)
        {
            var count = await prints.CountAsync(ct);
            var materialMg = await prints.SumAsync(PrintMetrics.MaterialMgExpr, ct);
            var timeSeconds = await prints.SumAsync(PrintMetrics.DurationSecondsExpr, ct);
            var estimatedDuration = await prints.CountAsync(PrintMetrics.DurationIsEstimatedExpr, ct);
            var estimatedMaterial = await prints.CountAsync(PrintMetrics.MaterialIsEstimatedExpr, ct);

            return new SummaryMetrics(
                count, McpUnits.MgToGrams(materialMg), timeSeconds, estimatedDuration, estimatedMaterial);
        }

        public async Task<PrintSummaryResult> GetPrintSummaryForMcp(
            long userId, DateTimeOffset? from, DateTimeOffset? to,
            Print.PrintStatus? status, CancellationToken ct)
        {
            var owned = _context.Prints.AsNoTracking().Where(p => p.CreatedById == userId);

            // An explicit range covers dated prints inside it. All-time covers EVERY print, including
            // undated ones — which is why the undated block below exists to reconcile the two.
            var hasRange = from.HasValue && to.HasValue;
            // Same condition as hasRange, written as a pattern so the compiler carries the two
            // dates into the lambda. hasRange itself stays: the undated query below still needs it
            // as a plain bool.
            var inScope = from is { } rangeStart && to is { } rangeEnd
                ? owned.Where(p => p.StartDate >= rangeStart && p.StartDate <= rangeEnd)
                : owned;

            var filtered = status.HasValue ? inScope.Where(p => p.Status == status.Value) : inScope;

            var statusCounts = await inScope
                .GroupBy(p => p.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Status.ToString(), x => x.Count, ct);

            // Zero-count statuses must be present. An absent key reads as "unknown", not "none".
            foreach (var name in Enum.GetNames<Print.PrintStatus>())
            {
                statusCounts.TryAdd(name, 0);
            }

            var undatedQuery = hasRange
                ? owned.Where(p => false) // a ranged query reports zeros, so the shape never changes
                : owned.Where(p => p.StartDate == null);

            if (status.HasValue)
            {
                undatedQuery = undatedQuery.Where(p => p.Status == status.Value);
            }

            return new PrintSummaryResult(
                from,
                to,
                status?.ToString(),
                await Aggregate(filtered, ct),
                statusCounts,
                await Aggregate(undatedQuery, ct));
        }
    }
}
