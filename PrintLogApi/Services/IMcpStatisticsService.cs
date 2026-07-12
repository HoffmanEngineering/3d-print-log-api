using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PrintLogApi.Mcp;

namespace PrintLogApi.Services
{
    /// <summary>
    /// Read-only, creator-only aggregate statistics for the MCP server. All aggregation runs in
    /// SQL over the caller's own prints; date ranges are inclusive UTC.
    /// </summary>
    public interface IMcpStatisticsService
    {
        Task<IReadOnlyList<PrinterStatsItem>> GetPrinterStats(
            long userId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

        Task<PrintSummaryResult> GetPrintSummaryForMcp(
            long userId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    }
}
