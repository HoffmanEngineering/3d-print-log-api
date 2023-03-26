using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.PrinterMaintenance;
using PrintLogApi.Models.SortEnums;

namespace PrintLogApi.Services
{
    public interface IPrinterMaintenanceService
    {
        Task<PrinterMaintenance> AddEntry(AddPrinterMaintenanceDto dto, long userId);
        Task DeleteMaintenanceEntry(PrinterMaintenance entry);
        Task<List<PrinterMaintenance>> GetEntriesByPrinterId(long printerId);
        Task<PrinterMaintenance> GetEntryById(Guid id);
        Task<string[]> GetMaintenanceCategories(long userId);
        Task<PagedList<PrinterMaintenanceDto>> GetPrinterMaintenanceByUser(long userId, SortDirection sortDirection, PrinterMaintenanceSortColumn sortColumn, int pageNumber, int pageSize, string searchText, long[] filterByPrinterIds, bool? includeDone = true, bool? includeNotDone = true);
        Task<PrinterMaintenance> UpdateEntry(Guid id, PutPrinterMaintenanceDto dto, long userId);
    }
}