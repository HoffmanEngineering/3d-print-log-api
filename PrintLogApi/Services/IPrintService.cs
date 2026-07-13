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

        /// <summary>
        /// Creator-only reprint cost estimate for the MCP server. Returns null when the print does
        /// not exist or is not owned by <paramref name="userId"/>. The API has no trustworthy
        /// server-side cost calculation in v1, so EstimatedCost is null; material grams, duration,
        /// and the user's preferred currency are still returned.
        /// </summary>
        Task<ReprintCostResult> EstimateReprintCostForMcp(long userId, long printId, CancellationToken ct);
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
