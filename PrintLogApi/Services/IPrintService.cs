using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.SortEnums;

namespace PrintLogApi.Services
{
    public interface IPrintService
    {
        Task<Print> AddPrint(AddPrintDTO print, long userId);
        Task<Comment> AddPrintComment(Print print, string commentBody, long userId);
        Task DeletePrint(Print existingPrint);
        Task<Stream> GeneratePrintReportAsCsvForUser(long userId);
        Task<Print> GetPrintById(long id);
        Task<List<PrintStatistic>> GetPrintStatisticsForUser(long userId, DateTimeOffset fromDate, DateTimeOffset toDate);
        Task<List<long>> GetPublicPrintIds();
        Task<PagedList<PrintSummaryDTO>> SearchPrintSummary(PagedRequest pagingRequest, string searchText, SortRequest<PrintSummarySortColumn> sortRequest, IEnumerable<long> filterByPrinterIds, Print.PrintStatus? filterByStatus, long? userId, long? currentUserId);
        Task<List<PrintFeedSummaryDto>> GetPrintFeedSummary(long? currentUserId, int numberOfRecords, DateTimeOffset fromDateTime);

        Task SetDefaultImage(long printId, long newDefaultImageId);
        Task<Print> UpdatePrint(long id, PutPrintDetailDto dto, long userId);
        Task<Print> UpdatePrintStatus(long id, Print.PrintStatus newStatus, long userId);
        Task UpdateFilamentUsageWeights(Print print);
    }
}
