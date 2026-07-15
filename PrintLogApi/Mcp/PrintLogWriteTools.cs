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
    /// The write MCP tool surface. Every tool runs as the token-derived user (resolved from the
    /// token, never a tool argument), enforces ownership in the service query, bounds its blast
    /// radius on the server, and never mutates printer loaded-state. The class-level
    /// <see cref="AuthorizeAttribute"/> requires the write:printdata scope on top of the endpoint's
    /// authentication policy.
    /// </summary>
    [McpServerToolType]
    [Authorize(Policy = "McpWrite")]
    public class PrintLogWriteTools
    {
        private readonly IHttpContextAccessor httpContextAccessor;
        private readonly IPrintService printService;
        private readonly IFilamentService filamentService;
        private readonly IProjectService projectService;

        public PrintLogWriteTools(
            IHttpContextAccessor httpContextAccessor,
            IPrintService printService,
            IFilamentService filamentService,
            IProjectService projectService)
        {
            this.httpContextAccessor = httpContextAccessor;
            this.printService = printService;
            this.filamentService = filamentService;
            this.projectService = projectService;
        }

        private long CurrentUserId =>
            McpUserContext.RequireUserId(httpContextAccessor.HttpContext!.User);

        [McpServerTool(Name = "whoami"), Description("Confirms write access is granted. Returns your internal user id.")]
        public long WhoAmI() => CurrentUserId;

        [McpServerTool, Description(
            "Log a finished 3D print for yourself. Records status, optional start time and duration " +
            "(seconds), notes, an optional projectId, and per-material usage. Each usage row is " +
            "{ materialId, source, amount } where source is Weight (grams), Length (mm), or Volume " +
            "(ml) — report the amount in whatever unit the slicer gave. 'idempotencyKey' MUST be a " +
            "stable id for this physical print: calling twice with the same key returns the SAME print " +
            "(wasReplayed = true) and never creates a duplicate. This does NOT change which spools are " +
            "loaded on the printer. A slicer integration may already have imported this print — confirm " +
            "with the user before logging if unsure. Only your own printer/materials/project are " +
            "accepted; anything else is 'not found'.")]
        public async Task<LogPrintResult> LogPrint(
            [Description("Print title (max 100 chars).")] string title,
            [Description("Your printer id (see list_printers).")] long printerId,
            [Description("Print status, e.g. Success, PartialSuccess, Failed.")] Print.PrintStatus status,
            [Description("Stable idempotency key for this print. Reusing it returns the same print.")] string idempotencyKey,
            [Description("Optional UTC start time.")] DateTimeOffset? startedAt = null,
            [Description("Optional measured duration in seconds (> 0).")] int? durationSeconds = null,
            [Description("Optional notes.")] string notes = null,
            [Description("Optional project id (see list_projects).")] Guid? projectId = null,
            [Description("Optional per-material usage rows.")] MaterialUsageInput[] materials = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                throw McpToolException.InvalidArguments("title is required.");
            }
            McpWriteValidation.RequireMaxLength(title, 100, "title");
            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                throw McpToolException.InvalidArguments("idempotencyKey is required.");
            }
            McpWriteValidation.RequireMaxLength(idempotencyKey, 200, "idempotencyKey");
            McpWriteValidation.RequireDefinedEnum(status, "status");
            if (durationSeconds.HasValue)
            {
                McpWriteValidation.RequirePositiveDuration(durationSeconds.Value);
            }

            var rows = materials ?? Array.Empty<MaterialUsageInput>();
            foreach (var row in rows)
            {
                McpWriteValidation.RequireDefinedEnum(row.Source, "materials.source");
                McpWriteValidation.RequirePositiveAmount(row.Amount);
            }

            return await printService.LogPrintForMcp(
                CurrentUserId, title, printerId, status, startedAt, durationSeconds, notes, projectId,
                rows, idempotencyKey.Trim(), ct);
        }
    }
}
