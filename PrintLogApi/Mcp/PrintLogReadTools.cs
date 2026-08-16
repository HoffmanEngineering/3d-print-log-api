using System.ComponentModel;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;
using PrintLogApi.Models;
using PrintLogApi.Services;

namespace PrintLogApi.Mcp;

/// <summary>
/// The read-only MCP tool surface. Every tool runs as the authenticated MCP user (resolved
/// from the token, never a tool argument); the class-level <see cref="AuthorizeAttribute"/> is
/// defense-in-depth on top of the endpoint's McpAccess policy.
/// </summary>
[McpServerToolType]
[Authorize(Policy = "McpRead")]
public class PrintLogReadTools(
    IHttpContextAccessor httpContextAccessor,
    IPrintService printService,
    IFilamentService filamentService,
    IMcpStatisticsService statisticsService,
    IPrinterService printerService,
    IProjectService projectService)
{
    private long CurrentUserId =>
        McpUserContext.RequireUserId(httpContextAccessor.HttpContext!.User);

    [McpServerTool(Title = "Ping", ReadOnly = true, OpenWorld = false),
     Description("Health check. Echoes the input.")]
    public string Ping([Description("Any string")] string message) => $"pong: {message}";

    [McpServerTool(Title = "Search Prints", ReadOnly = true, OpenWorld = false), Description(
        "Search your own 3D prints. Use 'query' to find a print by name — it is a " +
        "case-insensitive substring match over the print title AND its project name, so 'bench' " +
        "finds 'Dual Color 3D Benchy'. Other optional filters: status, printer id, material id, " +
        "and an inclusive UTC start-date range. Results are paginated (default 25, max 100 per " +
        "page) and ordered newest first. Weights are grams, durations are seconds. " +
        "'durationIsEstimated' is true when 'durationSeconds' came from the slicer's estimate " +
        "rather than a measured print time: say so rather than stating it as fact. A null " +
        "'durationSeconds' means no duration was ever recorded — say that, do not report it as " +
        "zero. 'materialIsEstimated' works the same way for filament weight.")]
    public Task<McpPage<PrintListItem>> SearchPrints(
        [Description("Optional text search over the print title and its project name. Case-insensitive substring.")] string? query = null,
        [Description("Optional print status filter. A finished print is Success, or PartialSuccess if it completed with defects: when the user asks what they 'finished' or 'completed', say which of the two you counted rather than silently picking one.")] Print.PrintStatus? status = null,
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
            normalizedFrom, normalizedTo, query, ct);
    }

    [McpServerTool(Title = "Get Print", ReadOnly = true, OpenWorld = false), Description(
        "Get the details of one of your own prints by id, including a per-material breakdown of " +
        "what it used. Only the print's creator can read it; any other id (including public " +
        "prints owned by someone else) returns not found. Weights are grams, durations are " +
        "seconds. Slicer settings are NOT structured fields, but prints imported through a " +
        "slicer integration carry a settings summary in 'notes' (layer height, line width, " +
        "print/infill/wall speeds, nozzle and bed temperature, infill density, supports): when " +
        "the user asks what settings a print used, read 'notes' and quote it. A null field means " +
        "the value was never recorded, not zero — say it is not recorded rather than reporting 0. " +
        "'durationIsEstimated' is true when 'durationSeconds' came from the slicer's estimate " +
        "rather than a measured print time: say so rather than stating it as fact. " +
        "'materialIsEstimated' works the same way for filament weight.")]
    public async Task<PrintDetailResult> GetPrint(
        [Description("The print id.")] long id,
        CancellationToken ct = default)
    {
        var userId = CurrentUserId;
        var result = await printService.GetOwnPrintDetailForMcp(userId, id, ct);
        return result ?? throw McpToolException.NotFound("Print not found.");
    }

    [McpServerTool(Title = "Get Material Inventory", ReadOnly = true, OpenWorld = false), Description(
        "List your own filament/material inventory with remaining weight in grams, including " +
        "where each spool is stored. Material and color filters match on whole words, so 'PLA' " +
        "also finds 'PLA (Polylactic Acid)', 'PLA+' and 'Silk PLA', and 'blue' also finds " +
        "'Light Blue'. Inactive spools are excluded unless includeInactive is true. A negative " +
        "remainingGrams means more filament has been logged as used than the spool started " +
        "with, which is a data problem worth reporting to the user. Paginated (default 25, max 100).")]
    public Task<McpPage<MaterialInventoryItem>> GetMaterialInventory(
        [Description("Optional material filter (e.g. PLA). Matches whole words, so PLA also finds 'PLA (Polylactic Acid)' and 'PLA+'.")] string? material = null,
        [Description("Optional color filter (e.g. blue). Matches whole words, so blue also finds 'Light Blue'.")] string? color = null,
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

    [McpServerTool(Name = "get_material", Title = "Get Material", ReadOnly = true, OpenWorld = false), Description(
        "Get one of your own materials in full: category, density, diameter, colors, temperatures, " +
        "cure times, purchase details, notes, and capacity. Weights are grams, lengths mm, volumes " +
        "ml, temperatures °C, cure times seconds. 'sourceUnit' names the measurement the capacity " +
        "was entered in (Weight/Length/Volume) — 'initialAmountInSourceUnit' is that authoritative " +
        "figure; the gram values are derived from it. 'remainingGrams' is 0 both for an empty spool " +
        "and for one with no tracked capacity: check 'hasNominalCapacity' before reporting 'none " +
        "left'. Materials belonging to anyone else are 'not found'.")]
    public async Task<MaterialDetail> GetMaterial(
        [Description("The material id (see get_material_inventory or find_material).")] Guid materialId,
        CancellationToken ct = default)
    {
        return await filamentService.GetOwnMaterialDetailForMcp(CurrentUserId, materialId, ct);
    }

    [McpServerTool(Title = "Find Material", ReadOnly = true, OpenWorld = false), Description(
        "Find your own filament spools matching a material and/or color, grouped by their exact " +
        "material and color. Filters match whole words, so 'PLA' also finds 'PLA+' and 'Silk PLA'. " +
        "Optionally pass requiredGrams to see which groups can supply it. " +
        "sufficientOnLargestSpool means a SINGLE spool holds enough, so the print can run " +
        "unattended. meetsRequirementByCombiningSpools means only the SUM of several spools is " +
        "enough, which needs a filament change mid-print: present that to the user as a " +
        "suggestion to confirm, never as a guarantee, because spools in a group can still differ " +
        "in brand and diameter. combinationForRequirement lists the specific spools that reach " +
        "the requirement. If candidatesTruncated is true the caller has more matching spools " +
        "than could be examined, and a null meetsRequirementByCombiningSpools means UNKNOWN, " +
        "not no: say the answer could not be determined rather than telling the user they lack " +
        "the material. Weights are grams.")]
    public Task<FindMaterialResult> FindMaterial(
        [Description("Optional material filter (e.g. PLA). Matches whole words.")] string? material = null,
        [Description("Optional color filter (e.g. blue). Matches whole words.")] string? color = null,
        [Description("Optional grams needed for the print (finite, > 0).")] double? requiredGrams = null,
        CancellationToken ct = default)
    {
        if (requiredGrams.HasValue)
        {
            McpValidation.RequirePositiveGrams(requiredGrams.Value);
        }

        return filamentService.FindMaterialForMcp(CurrentUserId, material, color, requiredGrams, ct);
    }

    [McpServerTool(Title = "Get Printer Stats", ReadOnly = true, OpenWorld = false), Description(
        "Get per-printer statistics for your own prints: print counts, success/failure counts, " +
        "success rate percent, and total print time in seconds. Omit 'from' and 'to' for all-time " +
        "statistics; an explicit range is inclusive UTC and at most 366 days, and excludes prints " +
        "with no start date. Only printers with prints in scope are included. Paginated " +
        "(default 25, max 100). " +
        "Durations use the measured print time when one was recorded, otherwise the slicer's " +
        "estimate. 'printsWithEstimatedDuration' says how many prints for that printer were " +
        "estimated rather than measured: when it is non-zero, present the total as approximate " +
        "and say how many were estimates. Never report an estimated total as a measured one.")]
    public Task<McpPage<PrinterStatsItem>> GetPrinterStats(
        [Description("Optional inclusive start of the UTC range. Omit with 'to' for all-time.")] DateTimeOffset? from = null,
        [Description("Optional inclusive end of the UTC range (at most 366 days after 'from').")] DateTimeOffset? to = null,
        [Description("Optional printer id filter.")] long? printerId = null,
        [Description("1-based page number.")] int page = 1,
        [Description("Page size (default 25, max 100).")] int? pageSize = null,
        CancellationToken ct = default)
    {
        var validPage = McpPaging.RequirePage(page);
        var validPageSize = McpPaging.ClampPageSize(pageSize);
        var (validFrom, validTo) = NormalizeOptionalRange(from, to);

        return statisticsService.GetPrinterStats(
            CurrentUserId, validFrom, validTo, printerId, validPage, validPageSize, ct);
    }

    [McpServerTool(Title = "Get Print Summary", ReadOnly = true, OpenWorld = false), Description(
        "Summarize your own prints. Omit 'from' and 'to' for all-time totals, which INCLUDE " +
        "prints that have no start date (reported separately under 'undated', so that all-time " +
        "equals the sum of any exhaustive set of date ranges plus 'undated'). An explicit range " +
        "is inclusive UTC and at most 366 days. The optional status filter scopes the 'filtered' " +
        "metrics only; 'unfilteredStatusCounts' always covers every status in the range and " +
        "includes zero counts. Weights are grams, durations are seconds. " +
        "Durations use the measured print time when one was recorded, otherwise the slicer's " +
        "estimate. 'printsWithEstimatedDuration' says how many prints in scope were estimated " +
        "rather than measured: when it is non-zero, present the total as approximate and say how " +
        "many were estimates. Never report an estimated total as a measured one. " +
        "'printsWithEstimatedMaterial' does the same for filament weight.")]
    public Task<PrintSummaryResult> GetPrintSummary(
        [Description("Optional inclusive start of the UTC range. Omit with 'to' for all-time.")] DateTimeOffset? from = null,
        [Description("Optional inclusive end of the UTC range (at most 366 days after 'from').")] DateTimeOffset? to = null,
        [Description("Optional status filter, e.g. Success. Scopes the 'filtered' metrics. A finished print is Success, or PartialSuccess if it completed with defects: when the user asks how many prints they 'finished' or 'completed', say which of the two you counted rather than silently picking one.")] Print.PrintStatus? status = null,
        CancellationToken ct = default)
    {
        var (validFrom, validTo) = NormalizeOptionalRange(from, to);

        return statisticsService.GetPrintSummaryForMcp(CurrentUserId, validFrom, validTo, status, ct);
    }

    [McpServerTool(Title = "List Printers", ReadOnly = true, OpenWorld = false), Description(
        "List your own 3D printers: id, name, make, model, nozzle diameter, and whether the " +
        "printer is active. Use this to resolve a printer you refer to by name into the id that " +
        "search_prints and get_printer_stats take. Paginated (default 25, max 100).")]
    public Task<McpPage<PrinterListItem>> ListPrinters(
        [Description("1-based page number.")] int page = 1,
        [Description("Page size (default 25, max 100).")] int? pageSize = null,
        CancellationToken ct = default)
    {
        var validPage = McpPaging.RequirePage(page);
        var validPageSize = McpPaging.ClampPageSize(pageSize);

        return printerService.ListPrintersForMcp(CurrentUserId, validPage, validPageSize, ct);
    }

    [McpServerTool(Title = "Get Printer", ReadOnly = true, OpenWorld = false), Description(
        "Get the full details of one of your own printers by id: description, nozzle diameter, " +
        "bed dimensions, heated bed/chamber, wattage, and the filament spools CURRENTLY loaded " +
        "on it (spools that have been unloaded are not included). Only the printer's owner can " +
        "read it; any other id returns not found. Weights are grams.")]
    public Task<PrinterDetailResult> GetPrinter(
        [Description("The printer id.")] long id,
        CancellationToken ct = default)
    {
        return printerService.GetPrinterForMcp(CurrentUserId, id, ct);
    }

    [McpServerTool(Title = "List Projects", ReadOnly = true, OpenWorld = false), Description(
        "List your own projects: id, name, reference, status, and visibility. Use this to resolve " +
        "a project name into the id that create_print and update_print take. Search matches name or " +
        "reference. Paginated (default 25, max 100), most-recently-updated first.")]
    public Task<McpPage<ProjectListItem>> ListProjects(
        [Description("Optional case-insensitive search over name and reference.")] string? search = null,
        [Description("Optional status filter.")] Project.ProjectStatus? status = null,
        [Description("1-based page number.")] int page = 1,
        [Description("Page size (default 25, max 100).")] int? pageSize = null,
        CancellationToken ct = default)
    {
        var validPage = McpPaging.RequirePage(page);
        var validPageSize = McpPaging.ClampPageSize(pageSize);
        return projectService.ListProjectsForMcp(CurrentUserId, validPage, validPageSize, search, status, ct);
    }

    /// <summary>
    /// Validates a date range only when one is actually supplied. Supplying just one endpoint is
    /// rejected rather than silently treated as all-time, which would quietly answer a different
    /// question than the caller asked.
    /// </summary>
    private static (DateTimeOffset? From, DateTimeOffset? To) NormalizeOptionalRange(
        DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from.HasValue != to.HasValue)
        {
            throw McpToolException.InvalidArguments(
                "Supply both 'from' and 'to', or neither (for all-time).");
        }

        // Both or neither, by the mismatch check above - so this tests `from` for the same
        // reason the original did, and picks up `to` for free.
        if (from is not { } fromValue || to is not { } toValue)
        {
            return (null, null);
        }

        var (validFrom, validTo) = McpValidation.RequireUtcRange(fromValue, toValue);
        return (validFrom, validTo);
    }

}
