using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;

namespace PrintLogApi.Services
{
    public class PrinterService : IPrinterService
    {
        private readonly PrintLogContext _context;

        public PrinterService(PrintLogContext context)
        {
            _context = context;
        }

        public async Task<Printer> getPrinterById(long printerId)
        {
            var existingPrinter = await _context.Printers
                .Include(p => p.LoadedFilaments)
                    .ThenInclude(f => f.Filament)
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
    }
}
