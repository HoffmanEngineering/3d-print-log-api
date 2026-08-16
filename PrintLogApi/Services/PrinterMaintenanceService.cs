using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using CsvHelper;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.PrinterMaintenance;
using PrintLogApi.Models.SortEnums;

namespace PrintLogApi.Services
{
    public class PrinterMaintenanceService : IPrinterMaintenanceService
    {
        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;
        private readonly TelemetryClient _telemetry;
        private readonly ICacheVersionService _cacheVersionService;

        public PrinterMaintenanceService(PrintLogContext context,
                            IMapper mapper,
                            TelemetryClient telemetry,
                            ICacheVersionService cacheVersionService)
        {
            _context = context;
            _mapper = mapper;
            _telemetry = telemetry;
            _cacheVersionService = cacheVersionService;
        }

        /// <summary>
        /// Maintenance feeds /api/analytics/printers, which caches per-printer maintenance cost
        /// for fifteen minutes. Without this bump, logging a service would leave the tab showing
        /// the old number for up to a quarter of an hour with no way to force a refresh.
        ///
        /// The owner comes from the PRINTER, never from a caller-supplied id: the write paths
        /// already prove the caller owns the machine, and re-deriving ownership from the entity
        /// is what keeps that proof and this invalidation from drifting apart.
        /// </summary>
        private async Task InvalidateAnalyticsCache(long printerId)
        {
            var ownerId = await _context.Printers
                .Where(p => p.Id == printerId)
                .Select(p => (long?)p.UserId)
                .FirstOrDefaultAsync();

            if (ownerId.HasValue) _cacheVersionService.InvalidateUserCache(ownerId.Value);
        }

        public async Task<List<PrinterMaintenance>> GetEntriesByPrinterId(long printerId)
        {
            var entry = await this._context.PrinterMaintenance
                .Include(pm => pm.Printer)
                .Where(pm => pm.PrinterId == printerId)
                .OrderByDescending(pm => pm.Date)
                .ThenByDescending(pm => pm.Id)
                .ToListAsync();

            return entry;
        }

        public async Task<PrinterMaintenance?> GetEntryById(Guid id)
        {
            var entry = await this._context.PrinterMaintenance
                .Include(pm => pm.Printer)
                .Where(p => p.Id == id)
                .FirstOrDefaultAsync();

            return entry;
        }

        public async Task<PagedList<PrinterMaintenanceDto>> GetPrinterMaintenanceByUser(
            long userId,
            SortDirection sortDirection,
            PrinterMaintenanceSortColumn sortColumn,
            int pageNumber,
            int pageSize,
            // Optional search filter; the IsNullOrWhiteSpace guard below has always handled the
            // null the controller binds when the query string omits it (#45).
            string? searchText,
            long[] filterByPrinterIds,
            bool? includeDone = true,
            bool? includeNotDone = true)
        {
            var printerMaintenance = _context.PrinterMaintenance
                .Where(f => f.CreatedById == userId);


            var maintenanceBaseQuery = printerMaintenance
                .ProjectTo<PrinterMaintenanceDto>(_mapper.ConfigurationProvider)
                .AsNoTracking();

            if (!string.IsNullOrWhiteSpace(searchText))
            {
                // Split on any spaces and search separately.
                var criterias = searchText.Split('"')
                     .Select((element, index) => index % 2 == 0  // If even index
                                           ? element.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)  // Split the item
                                           : new string[] { element })  // Keep the entire item
                     .SelectMany(element => element).ToList();
                foreach (var text in criterias)
                {
                    maintenanceBaseQuery = maintenanceBaseQuery.Where(f => f.Category!.Contains(text)
                        || f.Description!.Contains(text)
                        || f.Notes!.Contains(text));
                }

            }

            if (filterByPrinterIds.Length > 0)
            {
                maintenanceBaseQuery = maintenanceBaseQuery.Where(f => filterByPrinterIds.Contains(f.PrinterId));
            }


            if (includeDone.HasValue || includeNotDone.HasValue)
            {
                // If both Done and Not Done are requested, don't filter. If both Done and Not Done are excluded, also don't filter 
                if (includeDone.HasValue && includeNotDone.HasValue)
                {
                    if (includeDone.Value == includeNotDone.Value)
                    {
                        // Do Nothing with the query
                    } else if (includeDone.Value == true)
                    {
                        maintenanceBaseQuery = maintenanceBaseQuery.Where(f => f.Done == true);
                    } else
                    {
                        maintenanceBaseQuery = maintenanceBaseQuery.Where(f => f.Done == false);
                    }

                }
                else
                {
                    if (includeDone.HasValue)
                    {


                        maintenanceBaseQuery = maintenanceBaseQuery.Where(f => f.Done == true);

                    }

                    if (includeNotDone.HasValue)
                    {

                        maintenanceBaseQuery = maintenanceBaseQuery.Where(f => f.Done == false);

                    }
                }

            }



            if (sortColumn == PrinterMaintenanceSortColumn.Category)
            {
                {
                    if (sortDirection == SortDirection.Asc)
                    {
                        maintenanceBaseQuery = maintenanceBaseQuery.OrderBy(f => PrintLogContext.fnNaturalSort(f.Category!)).ThenBy(f => f.Date).ThenBy(f => f.Id);
                    }
                    else
                    {
                        maintenanceBaseQuery = maintenanceBaseQuery.OrderByDescending(f => PrintLogContext.fnNaturalSort(f.Category!)).ThenByDescending(f => f.Date).ThenByDescending(f => f.Id);
                    }
                }
            } else
            {
                if (sortDirection == SortDirection.Asc)
                {
                    maintenanceBaseQuery = maintenanceBaseQuery.OrderBy(f => f.Date).ThenBy(f => f.Category).ThenBy(f => f.Id);
                }
                else
                {
                    maintenanceBaseQuery = maintenanceBaseQuery.OrderByDescending(f => f.Date).ThenByDescending(f => f.Category).ThenByDescending(f => f.Id);
                    }
            }

            return await PagedList<PrinterMaintenanceDto>.CreateAsync(maintenanceBaseQuery, pageNumber, pageSize);

        }

        public async Task<PrinterMaintenance> AddEntry(AddPrinterMaintenanceDto dto, long userId)
        {
            var newEntry = _mapper.Map<PrinterMaintenance>(dto);

            var printer = await _context.Printers
                .Where(p => p.Id == newEntry.PrinterId)
                .FirstOrDefaultAsync();
            newEntry.Printer = printer ?? throw new UserCannotAccessPrinterException();

            // Check if the user had access to that printer!
            if (userId != printer.UserId)
            {
                //return BadRequest();
                throw new UserCannotAccessPrinterException();
            }

            newEntry.CreatedById = userId;
            newEntry.UpdatedById = userId;

            _context.PrinterMaintenance.Add(newEntry);
            await _context.SaveChangesAsync();

            _telemetry.TrackEvent("PrinterMaintenanceAdd");
            await InvalidateAnalyticsCache(newEntry.PrinterId);

            // Null-forgiven: the entry was just persisted, so the re-read always finds it.
            return (await GetEntryById(newEntry.Id))!;

        }

        public async Task<PrinterMaintenance> UpdateEntry(Guid id, PutPrinterMaintenanceDto dto, long userId)
        {
            var existingEntry = await GetEntryById(id);

            if (existingEntry == null)
            {
                throw new ArgumentNullException(nameof(id));
            }

            var updatedEntry = _mapper.Map<PutPrinterMaintenanceDto, PrinterMaintenance>(dto, existingEntry);

            var printer = await _context.Printers.FindAsync(dto.PrinterId);
            updatedEntry.Printer = printer!;

            // Check if the user had access to that printer!
            // Null-forgiven: an unknown PrinterId already threw here before nullable analysis was
            // enabled, and it fails closed either way. Tracked in #39.
            if (userId != printer!.UserId)
            {
                //return BadRequest();
                throw new UserCannotAccessPrinterException();
            }

            updatedEntry.UpdatedById = userId;


            _context.Entry(updatedEntry).State = EntityState.Modified;

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!PrinterMaintenanceEntryExists(id))
                {
                    throw new DoesNotExistException();
                }
                else
                {
                    throw;
                }
            }

            _telemetry.TrackEvent("PrinterMaintenanceEdit");
            await InvalidateAnalyticsCache(updatedEntry.PrinterId);

            // Null-forgiven: the entry was just persisted, so the re-read always finds it.
            return (await GetEntryById(updatedEntry.Id))!;
        }


        public async Task<string[]> GetMaintenanceCategories(long userId)
        {
            return await _context.PrinterMaintenance
                .Where(f => f.CreatedById == userId)
                .Where(f => f.Category != null && f.Category != "")
                .Select(f => f.Category!)
                .Distinct()
                .OrderBy(s => s)
                .ToArrayAsync();
        }

        private bool PrinterMaintenanceEntryExists(Guid id)
        {
            return _context.PrinterMaintenance.Any(e => e.Id == id);
        }

        public async Task DeleteMaintenanceEntry(PrinterMaintenance entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            // Captured BEFORE the delete: afterwards the row is gone and the owner is no longer
            // derivable from it.
            var printerId = entry.PrinterId;

            _context.PrinterMaintenance.Remove(entry);

            await _context.SaveChangesAsync();

            _telemetry.TrackEvent("PrinterMaintenanceDelete");
            await InvalidateAnalyticsCache(printerId);
        }
    }


}
