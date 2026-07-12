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

        public async Task<IReadOnlyList<PrinterStatsItem>> GetPrinterStats(
            long userId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        {
            var prints = _context.Prints.AsNoTracking()
                .Where(p => p.CreatedById == userId
                    && p.Printer.UserId == userId
                    && p.StartDate >= from && p.StartDate <= to);

            var grouped = await prints
                .GroupBy(p => p.PrinterId)
                .Select(g => new
                {
                    PrinterId = g.Key,
                    TotalPrints = g.Count(),
                    SuccessfulPrints = g.Count(p => p.Status == Print.PrintStatus.Success),
                    FailedPrints = g.Count(p => p.Status == Print.PrintStatus.Failed),
                    TotalPrintTimeSeconds = g.Sum(p => p.PrintTimeInSeconds ?? 0),
                })
                .ToListAsync(ct);

            var printerIds = grouped.Select(g => g.PrinterId).ToList();
            var names = await _context.Printers.AsNoTracking()
                .Where(p => printerIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

            return grouped
                .Select(g => new PrinterStatsItem(
                    g.PrinterId,
                    names.TryGetValue(g.PrinterId, out var name) ? name : null,
                    g.TotalPrints,
                    g.SuccessfulPrints,
                    g.FailedPrints,
                    McpUnits.SuccessRatePercent(g.SuccessfulPrints, g.TotalPrints),
                    g.TotalPrintTimeSeconds))
                .OrderBy(s => s.PrinterName, StringComparer.Ordinal)
                .ThenBy(s => s.PrinterId)
                .ToList();
        }

        public async Task<PrintSummaryResult> GetPrintSummaryForMcp(
            long userId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        {
            var prints = _context.Prints.AsNoTracking()
                .Where(p => p.CreatedById == userId && p.StartDate >= from && p.StartDate <= to);

            var total = await prints.CountAsync(ct);
            var successful = await prints.CountAsync(p => p.Status == Print.PrintStatus.Success, ct);
            var failed = await prints.CountAsync(p => p.Status == Print.PrintStatus.Failed, ct);
            // Canonical material usage from the per-filament rows (the scalar Print.FilamentUsageMg
            // is legacy and not maintained). Actual weight with an estimated-weight fallback.
            var materialMg = await prints.SumAsync(p => (long)p.FilamentUsage.Sum(pf =>
                pf.AmountMg.HasValue && pf.AmountMg > 0 ? pf.AmountMg.Value
                : pf.EstimatedAmountMg.HasValue && pf.EstimatedAmountMg > 0 ? pf.EstimatedAmountMg.Value
                : 0), ct);
            var timeSeconds = await prints.SumAsync(p => p.PrintTimeInSeconds ?? 0, ct);

            return new PrintSummaryResult(
                from, to, total, successful, failed, McpUnits.MgToGrams(materialMg), timeSeconds);
        }
    }
}
