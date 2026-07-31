using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PrintLogApi.Mcp;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.SortEnums;

namespace PrintLogApi.Services
{
    public interface IPrintService
    {
        Task<Print> AddPrint(AddPrintDTO print, long userId);

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
            long userId, string title, long printerId, Print.PrintStatus status,
            DateTimeOffset? startedAt, int? durationSeconds, int? estimatedDurationSeconds,
            string notes, Guid? projectId, string fileName, string url,
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
            long userId, long printId, string title, Print.PrintStatus? status, string notes,
            DateTimeOffset? startedAt, long? printerId, int? durationSeconds, int? estimatedDurationSeconds,
            string fileName, string url, Print.PrintViewStatus? viewStatus, bool? allowComments,
            bool? allowFileDownloads, Guid? projectId, bool materialsProvided,
            IReadOnlyList<MaterialUsageInput> materials, ISet<string> clearFields, CancellationToken ct);

        /// <summary>
        /// Read-only, creator-only, paginated print search for the MCP server. Filters are applied
        /// before paging; results are ordered StartDate DESC, Id DESC. Units are grams and seconds.
        /// </summary>
        Task<McpPage<PrintListItem>> SearchOwnPrintsForMcp(
            long userId, int page, int pageSize, Print.PrintStatus? status, long? printerId,
            Guid? filamentId, DateTimeOffset? from, DateTimeOffset? to, string query,
            CancellationToken ct);

        /// <summary>
        /// Creator-only print detail for the MCP server. Returns null when the print does not exist
        /// OR is not owned by <paramref name="userId"/> (no existence oracle for foreign prints,
        /// even public/unlisted ones). Excludes images, comments, files, and URLs.
        /// </summary>
        Task<PrintDetailResult> GetOwnPrintDetailForMcp(long userId, long printId, CancellationToken ct);

        Task<Comment> AddPrintComment(Print print, string commentBody, long userId);
        Task DeletePrint(Print existingPrint);
        Task<Stream> GeneratePrintReportAsCsvForUser(long userId);
        Task<Print> GetPrintById(long id);
        Task<List<PrintStatistic>> GetPrintStatisticsForUser(long userId, DateTimeOffset fromDate, DateTimeOffset toDate);
        Task<List<long>> GetPublicPrintIds();
        /// <summary>
        /// The statuses and projectIds collections replace the former scalar filters; the caller
        /// folds any legacy scalar parameters into them so there is a single code path here.
        /// The date range is half-open [fromDate, toDate).
        /// </summary>
        Task<PagedList<PrintSummaryDTO>> SearchPrintSummary(PagedRequest pagingRequest, string searchText, SortRequest<PrintSummarySortColumn> sortRequest, IEnumerable<long> filterByPrinterIds, IEnumerable<Guid> filterByFilamentIds, IReadOnlyCollection<Print.PrintStatus> statuses, long? userId, long? currentUserId, IReadOnlyCollection<Guid> projectIds = null, DateTimeOffset? fromDate = null, DateTimeOffset? toDate = null);
        Task<List<PrintFeedSummaryDto>> GetPrintFeedSummary(long? currentUserId, int numberOfRecords, DateTimeOffset fromDateTime);
        Task<PagedList<GroupedFeedItemDto>> GetGroupedFeedAsync(
            int pageNumber,
            int pageSize,
            long userId,
            string searchText = null,
            IEnumerable<long> filterByPrinterIds = null,
            IEnumerable<Guid> filterByFilamentIds = null,
            Print.PrintStatus? filterByStatus = null,
            SortRequest<PrintSummarySortColumn> sortRequest = null);

        Task<int> GetMaxImagesPerPrint(long userId);
        Task SetDefaultImage(long printId, int newDefaultImageId);
        Task<Print> UpdatePrint(long id, PutPrintDetailDto dto, long userId);
        Task<Print> UpdatePrintStatus(long id, Print.PrintStatus newStatus, long userId);
        Task UpdateFilamentUsageWeights(Print print);
    }
}
