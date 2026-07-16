using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Exceptions;
using PrintLogApi.Mcp;
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
                .Include(p => p.Category)
                    .ThenInclude(type => type.MaterialCategory)
                .Where(p => p.Id == printerId)
                .AsSplitQuery()
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

        /// <summary>
        /// Cap on the loaded spools returned by get_printer. An AMS/toolchanger setup can carry
        /// several at once; the count and truncation flag make any omission visible, because
        /// silently dropping a loaded spool from "what is loaded right now" is a wrong answer.
        /// </summary>
        public const int MaxLoadedFilaments = 10;

        public async Task<McpPage<PrinterListItem>> ListPrintersForMcp(
            long userId, int page, int pageSize, CancellationToken ct)
        {
            // Printer ownership is UserId (Filament ownership is CreatedById - they differ).
            var query = _context.Printers.AsNoTracking()
                .Where(p => p.UserId == userId)
                .OrderBy(p => p.Name)
                .ThenBy(p => p.Id);

            var totalCount = await query.CountAsync(ct);

            var items = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(p => new PrinterListItem(
                    p.Id, p.Name, p.Make, p.Model, p.NozzleDiameter, p.IsActive))
                .ToListAsync(ct);

            var totalPages = pageSize > 0 ? (int)Math.Ceiling(totalCount / (double)pageSize) : 0;
            return new McpPage<PrinterListItem>(items, page, pageSize, totalCount, totalPages);
        }

        public async Task<PrinterDetailResult> GetPrinterForMcp(
            long userId, long printerId, CancellationToken ct)
        {
            var row = await _context.Printers.AsNoTracking()
                .Where(p => p.Id == printerId && p.UserId == userId) // creator-only; no existence oracle
                .Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.Make,
                    p.Model,
                    p.Description,
                    p.CategoryNickname,
                    p.NozzleDiameter,
                    p.BedWidthMm,
                    p.BedDepthMm,
                    p.BedHeightMm,
                    p.HasHeatedBed,
                    p.HasHeatedChamber,
                    p.WattageW,
                    p.IsActive,
                    p.FilamentDiameter,
                    p.BeamDiameter,
                    p.ScreenResolutionXPixels,
                    p.ScreenResolutionYPixels,

                    // "Loaded" means CURRENTLY loaded. PrinterFilament keeps historical rows, so
                    // without the UnloadedDateTime filter every spool ever mounted would be reported
                    // as loaded now.
                    LoadedCount = p.LoadedFilaments.Count(pf =>
                        pf.UnloadedDateTime == null && pf.Filament.CreatedById == userId),

                    // A corrupt row can reference another user's spool. Its material, colour and
                    // remaining weight all live on that foreign row, so a redacted entry would carry
                    // no usable information - exclude it, but count it so the omission is visible.
                    ExcludedCount = p.LoadedFilaments.Count(pf =>
                        pf.UnloadedDateTime == null && pf.Filament.CreatedById != userId),

                    Loaded = p.LoadedFilaments
                        .Where(pf => pf.UnloadedDateTime == null && pf.Filament.CreatedById == userId)
                        .OrderByDescending(pf => pf.LoadedDateTime)
                        .ThenBy(pf => pf.Id)
                        .Select(pf => new
                        {
                            pf.FilamentId,
                            Name = pf.Filament.DisplayName,
                            Brand = pf.Filament.Brand,
                            Material = pf.Filament.MaterialType,
                            Color = pf.Filament.ColorName,
                            pf.Filament.DiameterMm,

                            // Same remaining-weight expression AutoMapper uses for FilamentSummaryDto:
                            // initial weight, minus usage (actual, falling back to estimate), plus
                            // adjustments.
                            RemainingMg = (pf.Filament.InitialNominalWeightMg ?? 0)
                                - pf.Filament.PrintFilaments.Sum(u =>
                                    u.AmountMg.HasValue && u.AmountMg > 0 ? (long)u.AmountMg
                                    : u.EstimatedAmountMg.HasValue && u.EstimatedAmountMg > 0 ? (long)u.EstimatedAmountMg
                                    : 0L)
                                + pf.Filament.FilamentAdjustments.Sum(adj => adj.AmountMg),

                            pf.LoadedDateTime,
                        })
                        // Capped in SQL, not after materialization: LoadedCount above already
                        // reports the true total, so there is no reason to pull every row back.
                        .Take(MaxLoadedFilaments)
                        .ToList(),
                })
                .FirstOrDefaultAsync(ct);

            if (row is null)
            {
                throw McpToolException.NotFound("Printer not found.");
            }

            var loaded = row.Loaded
                .Select(f => new LoadedFilament(
                    f.FilamentId, f.Name, f.Brand, f.Material, f.Color, f.DiameterMm,
                    McpUnits.MgToGrams(f.RemainingMg), f.LoadedDateTime))
                .ToList();

            // Named arguments deliberately: this record has 22 fields, many of them double? or
            // bool?, so a positional mix-up would compile cleanly and silently return the bed depth
            // as the bed width.
            return new PrinterDetailResult(
                Id: row.Id,
                // Name is non-null in this contract but only length-limited on the entity, so a
                // legacy row can hold null. Normalize rather than throw.
                Name: row.Name ?? string.Empty,
                Make: row.Make,
                Model: row.Model,
                Description: row.Description,
                CategoryNickname: row.CategoryNickname,
                NozzleDiameterMm: row.NozzleDiameter,
                BedWidthMm: row.BedWidthMm,
                BedDepthMm: row.BedDepthMm,
                BedHeightMm: row.BedHeightMm,
                HasHeatedBed: row.HasHeatedBed,
                HasHeatedChamber: row.HasHeatedChamber,
                WattageW: row.WattageW,
                IsActive: row.IsActive,
                LoadedFilaments: loaded,
                LoadedFilamentCount: row.LoadedCount,
                LoadedFilamentsTruncated: row.LoadedCount > MaxLoadedFilaments,
                ExcludedUnreadableSpools: row.ExcludedCount,
                FilamentDiameterMm: row.FilamentDiameter,
                BeamDiameterMm: row.BeamDiameter,
                ScreenResolutionXPixels: row.ScreenResolutionXPixels,
                ScreenResolutionYPixels: row.ScreenResolutionYPixels);
        }
    }
}
