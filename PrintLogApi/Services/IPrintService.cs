using PrintLogApi.Mcp;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Sitemap;
using PrintLogApi.Models.SortEnums;

namespace PrintLogApi.Services;

public interface IPrintService
{
    /// <summary>
    /// Adds a print. <paramref name="sourceCandidate"/> is how the caller reached the API (web or
    /// API key); a valid <see cref="AddPrintDTO.CuraSettingId"/> owned by the user overrides it
    /// with <see cref="PrintSource.SlicerPlugin"/>.
    /// </summary>
    Task<Print> AddPrint(AddPrintDTO print, long userId, PrintSource sourceCandidate = PrintSource.Web);

    /// <summary>
    /// The REST create. Without an external pair it is <see cref="AddPrint"/>. With one that
    /// <paramref name="userId"/> already used, it returns that print with WasReplayed = true
    /// instead of creating a duplicate.
    /// </summary>
    Task<AddPrintResult> CreatePrint(AddPrintDTO print, long userId, PrintSource sourceCandidate);

    /// <summary>
    /// Creates a print and its PrintFilament usage rows for the MCP write surface, in one
    /// transaction keyed by <paramref name="idempotencyKey"/>. Printer, project and every material
    /// must belong to <paramref name="userId"/> (else NotFound). Does NOT mutate printer
    /// loaded-state. Invalidates the user cache after commit. On replay (same user+tool+key)
    /// returns the existing print with WasReplayed = true, provided the arguments match the ones
    /// the key was first used with; a different payload under the same key is a Conflict.
    /// viewStatus/allowComments fall back to the user's saved defaults when not supplied.
    /// </summary>
    Task<CreatePrintResult> CreatePrintForMcp(
        long userId, string? title, long printerId, Print.PrintStatus status,
        DateTimeOffset? startedAt, int? durationSeconds, int? estimatedDurationSeconds,
        string? notes, Guid? projectId, string? fileName, string? url,
        Print.PrintViewStatus? viewStatus, bool? allowComments, bool? allowFileDownloads,
        IReadOnlyList<MaterialUsageInput> materials, string idempotencyKey, CancellationToken ct);

    /// <summary>
    /// Creator-only edit of a print for the MCP write surface. Only supplied fields change; a
    /// null return from GetOwnPrintDetailForMcp or a missing/foreign print surfaces NotFound.
    /// When <paramref name="materialsProvided"/> is true the usage list is fully replaced.
    /// <paramref name="clearFields"/> names nullable fields to null out; setting and clearing the
    /// same field is InvalidArguments. Everything is validated before any mutation, so a rejected
    /// edit leaves the print exactly as it was.
    /// </summary>
    Task<PrintDetailResult> UpdateOwnPrintForMcp(
        long userId, long printId, string? title, Print.PrintStatus? status, string? notes,
        DateTimeOffset? startedAt, long? printerId, int? durationSeconds, int? estimatedDurationSeconds,
        string? fileName, string? url, Print.PrintViewStatus? viewStatus, bool? allowComments,
        bool? allowFileDownloads, Guid? projectId, bool materialsProvided,
        IReadOnlyList<MaterialUsageInput>? materials, ISet<string> clearFields, CancellationToken ct);

    /// <summary>
    /// Read-only, creator-only, paginated print search for the MCP server. Filters are applied
    /// before paging; results are ordered StartDate DESC, Id DESC. Units are grams and seconds.
    /// </summary>
    Task<McpPage<PrintListItem>> SearchOwnPrintsForMcp(
        long userId, int page, int pageSize, Print.PrintStatus? status, long? printerId,
        Guid? filamentId, DateTimeOffset? from, DateTimeOffset? to, string? query,
        CancellationToken ct);

    /// <summary>
    /// Creator-only print detail for the MCP server. Returns null when the print does not exist
    /// OR is not owned by <paramref name="userId"/> (no existence oracle for foreign prints,
    /// even public/unlisted ones). Excludes images, comments, files, and URLs.
    /// </summary>
    Task<PrintDetailResult?> GetOwnPrintDetailForMcp(long userId, long printId, CancellationToken ct);

    Task<Comment> AddPrintComment(Print print, string commentBody, long userId);
    Task DeletePrint(Print existingPrint);
    /// <summary>
    /// Writes the user's print report as CSV directly to <paramref name="destination"/>, streaming
    /// the rows so peak memory stays flat regardless of how many prints the user has. The caller
    /// owns <paramref name="destination"/> and it is left open; for the export endpoint that is the
    /// response body, so headers must be set before this is awaited.
    /// </summary>
    Task WritePrintReportAsCsvForUser(long userId, Stream destination, CancellationToken cancellationToken = default);
    Task<Print?> GetPrintById(long id);

    /// <summary>
    /// The two users who may edit a print, read untracked without loading the print graph, or
    /// null when the print does not exist.
    /// </summary>
    Task<PrintEditors?> GetPrintEditors(long id);
    Task<List<PrintStatistic>> GetPrintStatisticsForUser(long userId, DateTimeOffset fromDate, DateTimeOffset toDate);
    Task<List<long>> GetPublicPrintIds();
    /// <summary>
    /// Every public print with its last-modified time, ordered by id, for the UI's sitemap
    /// generator. One indexed scan projected to two columns; no navigation is loaded.
    /// </summary>
    Task<List<SitemapEntryDto>> GetPublicPrintSitemapEntries(CancellationToken cancellationToken = default);
    /// <summary>
    /// The statuses and projectIds collections replace the former scalar filters; the caller
    /// folds any legacy scalar parameters into them so there is a single code path here.
    /// The date range is half-open [fromDate, toDate).
    /// </summary>
    Task<PagedList<PrintSummaryDTO>> SearchPrintSummary(PagedRequest pagingRequest, string? searchText, SortRequest<PrintSummarySortColumn> sortRequest, IEnumerable<long>? filterByPrinterIds, IEnumerable<Guid>? filterByFilamentIds, IReadOnlyCollection<Print.PrintStatus>? statuses, long? userId, long? currentUserId, IReadOnlyCollection<Guid>? projectIds = null, DateTimeOffset? fromDate = null, DateTimeOffset? toDate = null, IReadOnlyCollection<Guid>? tagIds = null);
    Task<List<PrintFeedSummaryDto>> GetPrintFeedSummary(long? currentUserId, int numberOfRecords, DateTimeOffset fromDateTime);
    Task<PagedList<GroupedFeedItemDto>> GetGroupedFeedAsync(
        int pageNumber,
        int pageSize,
        long userId,
        string? searchText = null,
        IEnumerable<long>? filterByPrinterIds = null,
        IEnumerable<Guid>? filterByFilamentIds = null,
        Print.PrintStatus? filterByStatus = null,
        SortRequest<PrintSummarySortColumn>? sortRequest = null);

    Task<int> GetMaxImagesPerPrint(long userId);
    Task SetDefaultImage(long printId, int newDefaultImageId);
    Task<Print> UpdatePrint(long id, PutPrintDetailDto dto, long userId);

    /// <summary>
    /// Changes only the fields <paramref name="dto"/> sends, and nulls the ones it names in
    /// Clear. Sent usage rows replace the print's rows. Throws DoesNotExistException for a missing
    /// print or project, and UserCannotAccessPrinterException or UserCannotAccessFilamentException
    /// for a printer or filament the user does not own; nothing is written when it throws.
    /// </summary>
    Task<Print> PatchPrint(long id, PatchPrintDto dto, long userId);

    /// <summary>
    /// Records how a job ended: status, duration, and actual usage merged by filament (see
    /// CompletePrintDto). Leaves every other field alone, and is safe to repeat: remaining filament
    /// follows the rows, so a second identical call changes nothing. Notifies the print's creator
    /// only when this moves it from a not-finished status to a finished one (IPrintFinishNotifier),
    /// so a repeat does not notify again. Throws BadRequestException when endedAt is before the
    /// start date.
    /// </summary>
    Task<Print> CompletePrint(long id, CompletePrintDto dto, long userId);

    /// <summary>
    /// Links a connector's job to a print something else created (#165): sets the external pair,
    /// the connection when <see cref="ClaimPrintDto.ConnectionInstanceId"/> is one of the user's,
    /// and the start date when the print has none. <see cref="Print.Source"/> is kept. Only the
    /// print's creator may claim it, only while it is Printing and has no pair yet. Claiming again
    /// with the pair it already has returns it with WasReplayed = true. Throws
    /// DoesNotExistException, ForbiddenException, or PrintClaimConflictException when the print
    /// has another pair, is not Printing, or the pair is already on another of the user's prints.
    /// </summary>
    Task<AddPrintResult> ClaimPrint(long id, ClaimPrintDto dto, long userId);

    /// <summary>
    /// The user's prints on <paramref name="printerId"/> a connector could claim: Printing, with
    /// no external pair, created after <paramref name="createdAfter"/> when given. Newest first,
    /// at most <paramref name="limit"/>.
    /// </summary>
    Task<List<ClaimablePrintDto>> GetClaimablePrints(long userId, long printerId, DateTimeOffset? createdAfter, int limit, CancellationToken ct);
    Task<Print> UpdatePrintStatus(long id, Print.PrintStatus newStatus, long userId);
    Task UpdateFilamentUsageWeights(Print print);

    /// <summary>
    /// Applies one set of field values to many prints. Validates shared inputs first and
    /// throws <see cref="Exceptions.BulkRequestInvalidException"/> without writing anything;
    /// otherwise resolves each id to a per-id outcome and saves once.
    /// </summary>
    Task<BulkPrintOperationResult> BulkUpdatePrints(long userId, BulkUpdatePrintsDto dto, CancellationToken ct);

    /// <summary>
    /// Permanently deletes many prints. Ids that no longer exist are reported as succeeded:
    /// the goal state is that the print is gone, which makes a retry after a lost response
    /// report the truth. Only the print's creator may delete it.
    /// </summary>
    Task<BulkPrintOperationResult> BulkDeletePrints(long userId, IReadOnlyList<long> printIds, CancellationToken ct);
}

/// <summary>A created print, or the existing one an external pair resolved to.</summary>
public sealed record AddPrintResult(Print Print, bool WasReplayed);

/// <summary>A print's creator and its printer's owner: the users who may edit it.</summary>
public sealed record PrintEditors(long CreatedById, long PrinterOwnerId);
