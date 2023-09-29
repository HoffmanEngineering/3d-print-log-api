using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;

namespace PrintLogApi.Services
{
    public class PrinterService : IPrinterService
    {
        private readonly PrintLogContext _context;
        private readonly TelemetryClient _telemetry;
        private readonly IPrinterCategoryService _printerCategoryService;

        public PrinterService(PrintLogContext context, TelemetryClient telemetry, IPrinterCategoryService printerCategoryService)
        {
            _context = context;
            _telemetry = telemetry;
            _printerCategoryService = printerCategoryService;
        }

        public async Task<Printer> getPrinterById(long printerId)
        {
            var existingPrinter = await _context.Printers
                .Include(p => p.LoadedFilaments)
                    .ThenInclude(f => f.Filament)
                .Include(p => p.Type)
                    .ThenInclude(type => type.MaterialCategory)
                .Where(p => p.Id == printerId)
                .SingleOrDefaultAsync();

            return existingPrinter;
        }

        /// <summary>
        /// Sets a list of loadedFilamentIds as a printer's currently loaded filaments.
        /// </summary>
        /// <param name="printerId"></param>
        /// <param name="loadedFilamentIds"></param>
        /// <returns></returns>
        public async Task setLoadedFilament(long printerId, IEnumerable<Guid> loadedFilamentIds)
        {
            var printer = await getPrinterById(printerId);

            if (printer == null)
            {
                throw new PrinterDoesNotExistException();
            }

            // Handle managing loaded filaments for this printer.
            // Unload any filament that isn't in the new print.
            var currentlyLoadedFilament = printer.LoadedFilaments;
            var filamentsToUnload = currentlyLoadedFilament
                .Where(f => !loadedFilamentIds.Any(id => id == f.FilamentId));

            var modifiedTime = DateTimeOffset.Now;
            foreach (var filament in filamentsToUnload)
            {
                filament.UnloadedDateTime = modifiedTime;
                _context.Entry(filament).State = EntityState.Modified;
            }

            // Add any new filament to the list
            var newFilament = loadedFilamentIds
                .Where(loadedFilamentId => !currentlyLoadedFilament.Any(f => f.FilamentId == loadedFilamentId));

            foreach (var filamentId in newFilament)
            {
                if (filamentId != default)
                {
                    var newLoadedFilament = new PrinterFilament
                    {
                        FilamentId = filamentId,
                        PrinterId = printer.Id,
                        LoadedDateTime = modifiedTime,
                    };
                    printer.LoadedFilaments.Add(newLoadedFilament);
                }
            }

            // Fixup for any loaded filaments with no set LoadedDateTime:
            foreach (var pf in printer.LoadedFilaments.Where(lf => lf.LoadedDateTime == default(DateTimeOffset)))
            {
                pf.LoadedDateTime = modifiedTime;
            }

            // Finally, unload these filament from any other printer's loaded list.
            var loadedFilamentFromOtherPrinters = await _context
                .PrinterFilament
                .Where(pf => pf.PrinterId != printerId && loadedFilamentIds.Any(id => id == pf.FilamentId))
                .ToListAsync();

            foreach (var filament in loadedFilamentFromOtherPrinters)
            {
                filament.UnloadedDateTime = modifiedTime;
            }

        }

        /// <summary>
        /// Delete a Printer if that printer isn't in use by a print.
        /// </summary>
        /// <param name="printerId"></param>
        /// <returns></returns>
        public async Task DeletePrinter(long printerId)
        {
            var printer = await getPrinterById(printerId);
            if (printer == null)
            {
                return;
            }

            var hasExistingPrints = await DoPrintsExistForPrinter(printerId);


            // Check if any filament is being used.
            if (hasExistingPrints)
            {
                throw new PrinterIsInUseException();
            }

            // Remove any adjustments
            if (printer.LoadedFilaments.Any())
            {
                _context.PrinterFilament.RemoveRange(printer.LoadedFilaments);
            }

            var maintenanceEntries = _context.PrinterMaintenance.Where(pm => pm.PrinterId == printerId);
            if (maintenanceEntries.Any())
            {
                _context.PrinterMaintenance.RemoveRange(maintenanceEntries);
            }

            _context.Printers.Remove(printer);

            await _context.SaveChangesAsync();

            _telemetry.TrackEvent("PrinterDelete");

            return;
        }

        private async Task<bool> DoPrintsExistForPrinter(long printerId)
        {
            var exists = await _context.Prints.AnyAsync(p => p.PrinterId == printerId);

            return exists;
        }
    }
}
