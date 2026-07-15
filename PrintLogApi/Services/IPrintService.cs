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
        /// returns the existing print with WasReplayed = true.
        /// </summary>
        Task<LogPrintResult> LogPrintForMcp(
            long userId, string title, long printerId, Print.PrintStatus status,
            DateTimeOffset? startedAt, int? durationSeconds, string notes, Guid? projectId,
            IReadOnlyList<MaterialUsageInput> materials, string idempotencyKey, CancellationToken ct);

        /// <summary>
        /// Creator-only edit of a print for the MCP write surface. Only supplied fields change; a
        /// null return from GetOwnPrintDetailForMcp or a missing/foreign print surfaces NotFound.
        /// When <paramref name="materialsProvided"/> is true the usage list is fully replaced. When
        /// <paramref name="projectProvided"/> is true the project link is set (null clears it).
        /// </summary>
        Task<PrintDetailResult> UpdateOwnPrintForMcp(
            long userId, long printId, Print.PrintStatus? status, string notes, int? durationSeconds,
            bool projectProvided, Guid? projectId,
            bool materialsProvided, IReadOnlyList<MaterialUsageInput> materials, CancellationToken ct);

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
        Task<PagedList<PrintSummaryDTO>> SearchPrintSummary(PagedRequest pagingRequest, string searchText, SortRequest<PrintSummarySortColumn> sortRequest, IEnumerable<long> filterByPrinterIds, IEnumerable<Guid> filterByFilamentIds, Print.PrintStatus? filterByStatus, long? userId, long? currentUserId, Guid? filterByProjectId = null);
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
