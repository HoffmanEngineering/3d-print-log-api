using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PrintLogApi.Models;
using PrintLogApi.Services;

namespace PrintLogApi.Mcp
{
    /// <summary>
    /// The read-only MCP tool surface. Every tool runs as the authenticated MCP user (resolved
    /// from the token, never a tool argument); the class-level <see cref="AuthorizeAttribute"/> is
    /// defense-in-depth on top of the endpoint's McpAccess policy.
    /// </summary>
    [McpServerToolType]
    [Authorize(Policy = "McpAccess")]
    public class PrintLogTools
    {
        private readonly IHttpContextAccessor httpContextAccessor;
        private readonly IPrintService printService;
        private readonly IFilamentService filamentService;
        private readonly IMcpStatisticsService statisticsService;

        public PrintLogTools(
            IHttpContextAccessor httpContextAccessor,
            IPrintService printService,
            IFilamentService filamentService,
            IMcpStatisticsService statisticsService)
        {
            this.httpContextAccessor = httpContextAccessor;
            this.printService = printService;
            this.filamentService = filamentService;
            this.statisticsService = statisticsService;
        }

        private long CurrentUserId =>
            McpUserContext.RequireUserId(httpContextAccessor.HttpContext!.User);

        [McpServerTool, Description("Health check. Echoes the input.")]
        public string Ping([Description("Any string")] string message) => $"pong: {message}";

        [McpServerTool, Description(
            "Search your own 3D prints. Optional filters: status, printer id, material id, and an " +
            "inclusive UTC start-date range. Results are paginated (default 25, max 100 per page) " +
            "and ordered newest first. Weights are grams, durations are seconds.")]
        public Task<McpPage<PrintListItem>> SearchPrints(
            [Description("Optional print status filter.")] Print.PrintStatus? status = null,
            [Description("Optional printer id filter.")] long? printerId = null,
            [Description("Optional material (filament) id filter.")] Guid? materialId = null,
            [Description("Optional inclusive start of the UTC start-date range.")] DateTimeOffset? from = null,
            [Description("Optional inclusive end of the UTC start-date range.")] DateTimeOffset? to = null,
            [Description("1-based page number.")] int page = 1,
            [Description("Page size (default 25, max 100).")] int? pageSize = null,
            CancellationToken ct = default)
        {
            var userId = CurrentUserId;
            var validPage = McpPaging.RequirePage(page);
            var validPageSize = McpPaging.ClampPageSize(pageSize);

            DateTimeOffset? normalizedFrom = from;
            DateTimeOffset? normalizedTo = to;
            if (from.HasValue && to.HasValue)
            {
                (normalizedFrom, normalizedTo) = McpValidation.RequireUtcRange(from.Value, to.Value);
            }

            return printService.SearchOwnPrintsForMcp(
                userId, validPage, validPageSize, status, printerId, materialId,
                normalizedFrom, normalizedTo, ct);
        }

        [McpServerTool, Description(
            "Get the details of one of your own prints by id. Only the print's creator can read it; " +
            "any other id (including public prints owned by someone else) returns not found. Weights " +
            "are grams, durations are seconds.")]
        public async Task<PrintDetailResult> GetPrint(
            [Description("The print id.")] long id,
            CancellationToken ct = default)
        {
            var userId = CurrentUserId;
            var result = await printService.GetOwnPrintDetailForMcp(userId, id, ct);
            return result ?? throw McpToolException.NotFound("Print not found.");
        }

        [McpServerTool, Description(
            "List your own filament/material inventory with remaining weight in grams, including " +
            "where each spool is stored. Material and color filters match on whole words, so 'PLA' " +
            "also finds 'PLA (Polylactic Acid)', 'PLA+' and 'Silk PLA', and 'blue' also finds " +
            "'Light Blue'. Inactive spools are excluded unless includeInactive is true. A negative " +
            "remainingGrams means more filament has been logged as used than the spool started " +
            "with, which is a data problem worth reporting to the user. Paginated (default 25, max 100).")]
        public Task<McpPage<MaterialInventoryItem>> GetMaterialInventory(
            [Description("Optional material filter (e.g. PLA). Matches whole words, so PLA also finds 'PLA (Polylactic Acid)' and 'PLA+'.")] string material = null,
            [Description("Optional color filter (e.g. blue). Matches whole words, so blue also finds 'Light Blue'.")] string color = null,
            [Description("Include inactive/archived spools. Defaults to false.")] bool includeInactive = false,
            [Description("1-based page number.")] int page = 1,
            [Description("Page size (default 25, max 100).")] int? pageSize = null,
            CancellationToken ct = default)
        {
            var userId = CurrentUserId;
            var validPage = McpPaging.RequirePage(page);
            var validPageSize = McpPaging.ClampPageSize(pageSize);
            return filamentService.GetMaterialInventoryForMcp(
                userId, validPage, validPageSize, material, color, includeInactive, ct);
        }

        [McpServerTool, Description(
            "Find your own filament spools matching a material and/or color, grouped by their exact " +
            "material and color. Filters match whole words, so 'PLA' also finds 'PLA+' and 'Silk PLA'. " +
            "Optionally pass requiredGrams to see which groups can supply it. " +
            "sufficientOnLargestSpool means a SINGLE spool holds enough, so the print can run " +
            "unattended. meetsRequirementByCombiningSpools means only the SUM of several spools is " +
            "enough, which needs a filament change mid-print: present that to the user as a " +
            "suggestion to confirm, never as a guarantee, because spools in a group can still differ " +
            "in brand and diameter. combinationForRequirement lists the specific spools that reach " +
            "the requirement. Weights are grams.")]
        public Task<FindMaterialResult> FindMaterial(
            [Description("Optional material filter (e.g. PLA). Matches whole words.")] string material = null,
            [Description("Optional color filter (e.g. blue). Matches whole words.")] string color = null,
            [Description("Optional grams needed for the print (finite, > 0).")] double? requiredGrams = null,
            CancellationToken ct = default)
        {
            if (requiredGrams.HasValue)
            {
                McpValidation.RequirePositiveGrams(requiredGrams.Value);
            }

            return filamentService.FindMaterialForMcp(CurrentUserId, material, color, requiredGrams, ct);
        }

        [McpServerTool, Description(
            "Get per-printer statistics for your own prints over an inclusive UTC date range " +
            "(maximum 366 days): print counts, success/failure counts, success rate percent, and " +
            "total print time in seconds. Only printers with prints in the range are included.")]
        public async Task<IReadOnlyList<PrinterStatsItem>> GetPrinterStats(
            [Description("Inclusive start of the UTC range.")] DateTimeOffset from,
            [Description("Inclusive end of the UTC range (at most 366 days after 'from').")] DateTimeOffset to,
            CancellationToken ct = default)
        {
            var userId = CurrentUserId;
            var (validFrom, validTo) = McpValidation.RequireUtcRange(from, to);
            return await statisticsService.GetPrinterStats(userId, validFrom, validTo, ct);
        }

        [McpServerTool, Description(
            "Summarize your own prints over an inclusive UTC date range (maximum 366 days): total, " +
            "successful, and failed print counts, total material used in grams, and total print time " +
            "in seconds.")]
        public async Task<PrintSummaryResult> GetPrintSummary(
            [Description("Inclusive start of the UTC range.")] DateTimeOffset from,
            [Description("Inclusive end of the UTC range (at most 366 days after 'from').")] DateTimeOffset to,
            CancellationToken ct = default)
        {
            var userId = CurrentUserId;
            var (validFrom, validTo) = McpValidation.RequireUtcRange(from, to);
            return await statisticsService.GetPrintSummaryForMcp(userId, validFrom, validTo, ct);
        }

        [McpServerTool, Description(
            "Estimate the cost to reprint one of your own prints. Only the print's creator can " +
            "estimate it; any other id (including public prints owned by someone else) returns not " +
            "found. Returns material grams, duration seconds, and your preferred currency. Note: v1 " +
            "does not compute a monetary cost, so estimatedCost is null.")]
        public async Task<ReprintCostResult> EstimateReprintCost(
            [Description("The print id to estimate a reprint for.")] long printId,
            CancellationToken ct = default)
        {
            var userId = CurrentUserId;
            var result = await printService.EstimateReprintCostForMcp(userId, printId, ct);
            return result ?? throw McpToolException.NotFound("Print not found.");
        }
    }
}
