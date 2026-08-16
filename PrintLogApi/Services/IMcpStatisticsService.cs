using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PrintLogApi.Mcp;
using PrintLogApi.Models;

namespace PrintLogApi.Services
{
    /// <summary>
    /// Read-only, creator-only aggregate statistics for the MCP server. All aggregation runs in
    /// SQL over the caller's own prints; date ranges are inclusive UTC.
    /// </summary>
    public interface IMcpStatisticsService
    {
        /// <summary>
        /// Per-printer statistics, paginated. Omit the date range for all-time; an explicit range is
        /// inclusive UTC and capped at 366 days. Ordering and paging happen in SQL.
        /// </summary>
        Task<McpPage<PrinterStatsItem>> GetPrinterStats(
            long userId, DateTimeOffset? from, DateTimeOffset? to, long? printerId,
            int page, int pageSize, CancellationToken ct);

        /// <summary>
        /// Print totals plus a full status breakdown. Omit the date range for all-time, which also
        /// includes prints with no start date (reported separately so the two reconcile).
        /// </summary>
        Task<PrintSummaryResult> GetPrintSummaryForMcp(
            long userId, DateTimeOffset? from, DateTimeOffset? to,
            Print.PrintStatus? status, CancellationToken ct);
    }
}
