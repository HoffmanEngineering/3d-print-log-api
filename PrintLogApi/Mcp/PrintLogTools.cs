using System;
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

        public PrintLogTools(IHttpContextAccessor httpContextAccessor, IPrintService printService)
        {
            this.httpContextAccessor = httpContextAccessor;
            this.printService = printService;
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
    }
}
