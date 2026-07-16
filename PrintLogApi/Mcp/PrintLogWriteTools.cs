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

        /// <summary>Upper bound on material-usage rows in one print write, so a single rate-limited
        /// call cannot submit an unbounded array. Far above any realistic multi-material print.</summary>
        private const int MaxMaterialRows = 50;

        [McpServerTool(Name = "whoami"), Description("Confirms write access is granted. Returns your internal user id.")]
        public long WhoAmI() => CurrentUserId;

        [McpServerTool(Name = "create_print"), Description(
            "Log a finished 3D print for yourself. Records status, optional start time and duration " +
            "(seconds), notes, an optional projectId, and per-material usage. Each usage row is " +
            "{ materialId, source, amount } where source is Weight (grams), Length (mm), or Volume " +
            "(ml) — report the amount in whatever unit the slicer gave. 'idempotencyKey' MUST be a " +
            "stable id for this physical print: calling twice with the same key returns the SAME print " +
            "(wasReplayed = true) and never creates a duplicate. This does NOT change which spools are " +
            "loaded on the printer. A slicer integration may already have imported this print — confirm " +
            "with the user before logging if unsure. Only your own printer/materials/project are " +
            "accepted; anything else is 'not found'.")]
        public async Task<CreatePrintResult> CreatePrint(
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
            if (rows.Length > MaxMaterialRows)
            {
                throw McpToolException.InvalidArguments($"At most {MaxMaterialRows} material rows are allowed.");
            }
            foreach (var row in rows)
            {
                if (!row.Source.HasValue || !row.Amount.HasValue)
                {
                    throw McpToolException.InvalidArguments("Each material row needs a source and amount.");
                }
                McpWriteValidation.RequireDefinedEnum(row.Source.Value, "materials.source");
                McpWriteValidation.RequirePositiveAmount(row.Amount.Value);
            }

            return await printService.CreatePrintForMcp(
                CurrentUserId, title, printerId, status, startedAt, durationSeconds, notes, projectId,
                rows, idempotencyKey.Trim(), ct);
        }

        [McpServerTool, Description(
            "Edit one of your own prints. Only fields you pass are changed. To move the print to a " +
            "project pass projectId; to remove it from its project pass clearProject = true. If you " +
            "pass 'materials' it REPLACES the print's entire material-usage list; omit it to leave " +
            "usage as-is. Only the print's creator can edit it; any other id is 'not found'.")]
        public async Task<PrintDetailResult> UpdatePrint(
            [Description("The print id.")] long id,
            [Description("Optional new status.")] Print.PrintStatus? status = null,
            [Description("Optional new notes.")] string notes = null,
            [Description("Optional new duration in seconds (> 0).")] int? durationSeconds = null,
            [Description("Optional project id to file the print under.")] Guid? projectId = null,
            [Description("Pass true to remove the print from its project. Ignored if projectId is set.")] bool clearProject = false,
            [Description("Optional replacement material-usage list. Omit to leave usage unchanged.")] MaterialUsageInput[] materials = null,
            CancellationToken ct = default)
        {
            if (status.HasValue)
            {
                McpWriteValidation.RequireDefinedEnum(status.Value, "status");
            }
            if (durationSeconds.HasValue)
            {
                McpWriteValidation.RequirePositiveDuration(durationSeconds.Value);
            }
            if (materials != null)
            {
                if (materials.Length > MaxMaterialRows)
                {
                    throw McpToolException.InvalidArguments($"At most {MaxMaterialRows} material rows are allowed.");
                }
                foreach (var row in materials)
                {
                    if (!row.Source.HasValue || !row.Amount.HasValue)
                    {
                        throw McpToolException.InvalidArguments("Each material row needs a source and amount.");
                    }
                    McpWriteValidation.RequireDefinedEnum(row.Source.Value, "materials.source");
                    McpWriteValidation.RequirePositiveAmount(row.Amount.Value);
                }
            }

            var projectProvided = projectId.HasValue || clearProject;
            var effectiveProjectId = projectId.HasValue ? projectId : (Guid?)null;

            return await printService.UpdateOwnPrintForMcp(
                CurrentUserId, id, status, notes, durationSeconds,
                projectProvided, effectiveProjectId,
                materialsProvided: materials != null, materials ?? Array.Empty<MaterialUsageInput>(), ct);
        }

        [McpServerTool, Description(
            "Add a new material to your inventory (filament, resin, powder, etc.). 'source' is how the " +
            "initial amount is measured: Weight (grams), Length (mm), or Volume (ml). " +
            "'materialCategoryNickname' must be one of your existing categories (e.g. 'filament', " +
            "'resin'); an unknown category is rejected. Categories that track a filament diameter " +
            "require diameterMm. colorHex is 6 hex digits with no leading '#'. Creates a single-color material.")]
        public async Task<MaterialInventoryItem> AddMaterial(
            [Description("Display name.")] string displayName,
            [Description("Material type, e.g. PLA, ABS, Resin.")] string materialType,
            [Description("Category nickname, e.g. filament or resin.")] string materialCategoryNickname,
            [Description("Density in g/cm^3 (> 0).")] double densityGramPerCubicCm,
            [Description("How the initial amount is measured.")] McpMeasurementSource source,
            [Description("Initial amount in the source's unit (g / mm / ml).")] double initialAmount,
            [Description("Diameter in mm. Required for diameter-tracking categories.")] double? diameterMm = null,
            [Description("Optional brand.")] string brand = null,
            [Description("Optional color name.")] string colorName = null,
            [Description("Optional color as 6 hex digits, no '#', e.g. 1188FF.")] string colorHex = null,
            [Description("Optional storage location.")] string storageLocation = null,
            [Description("Whether the material is active. Defaults to true.")] bool isActive = true,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                throw McpToolException.InvalidArguments("displayName is required.");
            }
            McpWriteValidation.RequireMaxLength(displayName, 255, "displayName");
            McpWriteValidation.RequireDefinedEnum(source, "source");

            return await filamentService.AddMaterialForMcp(
                CurrentUserId, displayName, materialType, materialCategoryNickname, densityGramPerCubicCm,
                diameterMm, source, initialAmount, brand, colorName, colorHex, storageLocation, isActive, ct);
        }

        [McpServerTool, Description(
            "Correct how much of one of your materials remains, by applying a delta (positive adds, " +
            "negative removes) measured as Weight (grams), Length (mm), or Volume (ml). The result " +
            "cannot go below zero or above the material's original capacity — an out-of-range " +
            "adjustment is rejected. Returns the before/after remaining in grams. Foreign materials " +
            "are 'not found'.")]
        public async Task<MaterialWriteResult> AdjustMaterialRemaining(
            [Description("The material id.")] Guid materialId,
            [Description("Unit of the delta.")] McpMeasurementSource source,
            [Description("Signed delta in the unit (g / mm / ml). Negative removes.")] double delta,
            [Description("Optional note explaining the adjustment.")] string notes = null,
            CancellationToken ct = default)
        {
            McpWriteValidation.RequireDefinedEnum(source, "source");
            McpWriteValidation.RequireMaxLength(notes, 1000, "notes");
            return await filamentService.AdjustMaterialRemainingForMcp(CurrentUserId, materialId, source, delta, notes, ct);
        }

        [McpServerTool, Description(
            "Activate or retire one of your materials. Retiring hides it from default inventory " +
            "listings but keeps its history. Foreign materials are 'not found'.")]
        public async Task<MaterialInventoryItem> SetMaterialActive(
            [Description("The material id.")] Guid materialId,
            [Description("True to activate, false to retire.")] bool isActive,
            CancellationToken ct = default)
        {
            return await filamentService.SetMaterialActiveForMcp(CurrentUserId, materialId, isActive, ct);
        }

        [McpServerTool, Description(
            "Create a new project to group prints under. Name is required (max 100 chars). viewStatus " +
            "controls visibility (Private, Unlisted, Public) and defaults to Private; the result echoes " +
            "the resulting visibility.")]
        public async Task<ProjectWriteResult> CreateProject(
            [Description("Project name (max 100 chars).")] string name,
            [Description("Optional external reference (max 100 chars).")] string reference = null,
            [Description("Optional description (max 5000 chars).")] string description = null,
            [Description("Optional URL (max 1000 chars).")] string url = null,
            [Description("Status, default InProgress.")] Project.ProjectStatus status = Project.ProjectStatus.InProgress,
            [Description("Visibility, default Private.")] Project.ProjectViewStatus viewStatus = Project.ProjectViewStatus.Private,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw McpToolException.InvalidArguments("name is required.");
            }
            ValidateProjectFields(name, reference, description, url, status, viewStatus);
            return await projectService.CreateProjectForMcp(CurrentUserId, name, reference, description, url, status, viewStatus, ct);
        }

        [McpServerTool, Description(
            "Edit one of your own projects. Only fields you pass are changed. viewStatus changes " +
            "visibility; the result echoes the resulting visibility. Foreign projects are 'not found'.")]
        public async Task<ProjectWriteResult> UpdateProject(
            [Description("The project id.")] Guid id,
            [Description("Optional new name (max 100 chars).")] string name = null,
            [Description("Optional new reference (max 100 chars).")] string reference = null,
            [Description("Optional new description (max 5000 chars).")] string description = null,
            [Description("Optional new URL (max 1000 chars).")] string url = null,
            [Description("Optional new status.")] Project.ProjectStatus? status = null,
            [Description("Optional new visibility.")] Project.ProjectViewStatus? viewStatus = null,
            CancellationToken ct = default)
        {
            ValidateProjectFields(name, reference, description, url, status, viewStatus);
            return await projectService.UpdateProjectForMcp(CurrentUserId, id, name, reference, description, url, status, viewStatus, ct);
        }

        private static void ValidateProjectFields(
            string name, string reference, string description, string url,
            Project.ProjectStatus? status, Project.ProjectViewStatus? viewStatus)
        {
            McpWriteValidation.RequireMaxLength(name, 100, "name");
            McpWriteValidation.RequireMaxLength(reference, 100, "reference");
            McpWriteValidation.RequireMaxLength(description, 5000, "description");
            McpWriteValidation.RequireMaxLength(url, 1000, "url");
            if (status.HasValue)
            {
                McpWriteValidation.RequireDefinedEnum(status.Value, "status");
            }
            if (viewStatus.HasValue)
            {
                McpWriteValidation.RequireDefinedEnum(viewStatus.Value, "viewStatus");
            }
        }
    }
}
