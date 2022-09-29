using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.SortEnums;

namespace PrintLogApi.Services
{
    public class FilamentService : IFilamentService
    {
        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;
        private readonly TelemetryClient _telemetry;

        public FilamentService(PrintLogContext context, IMapper mapper, TelemetryClient telemetry)
        {
            _context = context;
            _mapper = mapper;
            _telemetry = telemetry;
        }

        public async Task<PagedList<FilamentSummaryDto>> GetFilamentSummaryForUser(
            long userId,
            SortDirection sortDirection,
            FilamentSummarySortColumn sortColumn,
            int pageNumber,
            int pageSize,
            string searchText,
            bool? includeInactive,
            bool? showFavoritesOnly,
            bool? showLoadedFilamentOnly)
        {
            var filament = _context.Filaments
                .Where(f => f.CreatedById == userId);

                        // Filter out unloaded-filaments if requested.
            if (showLoadedFilamentOnly.HasValue && showLoadedFilamentOnly.Value == true)
            {
                filament = filament.Where(f => f.PrinterFilaments.Any(pf => !pf.UnloadedDateTime.HasValue));
            }

            var filamentsBase = filament
                .ProjectTo<FilamentSummaryDto>(_mapper.ConfigurationProvider)
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
                    filamentsBase = filamentsBase.Where(f => f.DisplayName.Contains(text) || f.Brand.Contains(text) || f.ColorName.Contains(text) || f.MaterialType.Contains(text) || f.Notes.Contains(text));
                }
                
            }

            // Filter out inactives unless specified
            if (!includeInactive.HasValue || includeInactive.Value == false)
            {
                filamentsBase = filamentsBase.Where(f => f.IsActive == true);
            }

            // Filter out non-favorites if requested.
            if (showFavoritesOnly.HasValue && showFavoritesOnly.Value == true)
            {
                filamentsBase = filamentsBase.Where(f => f.IsFavorite == true);
            }

            if (sortColumn == FilamentSummarySortColumn.DisplayName)
            {
                if (sortDirection == SortDirection.Asc)
                {
                    filamentsBase = filamentsBase.OrderBy(f => PrintLogContext.fnNaturalSort(f.DisplayName)).ThenBy(f => f.CreatedDate);
                } else
                {
                    filamentsBase = filamentsBase.OrderByDescending(f => PrintLogContext.fnNaturalSort(f.DisplayName)).ThenByDescending(f => f.CreatedDate);
                }
            } else if (sortColumn == FilamentSummarySortColumn.FilamentRemaining)
            {
                if (sortDirection == SortDirection.Asc)
                {
                    filamentsBase = filamentsBase.OrderBy(f => f.FilamentRemaining).ThenBy(f => f.DisplayName).ThenBy(f => f.CreatedDate);
                }
                else
                {
                    filamentsBase = filamentsBase.OrderByDescending(f => f.FilamentRemaining).ThenByDescending(f => f.DisplayName).ThenByDescending(f => f.CreatedDate);
                }
            } else if (sortColumn == FilamentSummarySortColumn.MaterialType)
            {
                if (sortDirection == SortDirection.Asc)
                {
                    filamentsBase = filamentsBase.OrderBy(f => f.MaterialType).ThenBy(f => f.DisplayName).ThenBy(f => f.CreatedDate);
                }
                else
                {
                    filamentsBase = filamentsBase.OrderByDescending(f => f.MaterialType).ThenByDescending(f => f.DisplayName).ThenByDescending(f => f.CreatedDate);
                }

            }
            else if (sortColumn == FilamentSummarySortColumn.Brand)
            {
                if (sortDirection == SortDirection.Asc)
                {
                    filamentsBase = filamentsBase.OrderBy(f => f.Brand).ThenBy(f => f.DisplayName).ThenBy(f => f.CreatedDate);
                }
                else
                {
                    filamentsBase = filamentsBase.OrderByDescending(f => f.Brand).ThenByDescending(f => f.DisplayName).ThenByDescending(f => f.CreatedDate);
                }

            }
            else if (sortColumn == FilamentSummarySortColumn.Color)
            {
                if (sortDirection == SortDirection.Asc)
                {
                    filamentsBase = filamentsBase.OrderBy(f => f.ColorName).ThenBy(f => f.DisplayName).ThenBy(f => f.CreatedDate);
                }
                else
                {
                    filamentsBase = filamentsBase.OrderByDescending(f => f.ColorName).ThenByDescending(f => f.DisplayName).ThenByDescending(f => f.CreatedDate);
                }

            }
            else if (sortColumn == FilamentSummarySortColumn.StorageLocation)
            {
                if (sortDirection == SortDirection.Asc)
                {
                    filamentsBase = filamentsBase.OrderBy(f => f.StorageLocation).ThenBy(f => f.DisplayName).ThenBy(f => f.CreatedDate);
                }
                else
                {
                    filamentsBase = filamentsBase.OrderByDescending(f => f.StorageLocation).ThenByDescending(f => f.DisplayName).ThenByDescending(f => f.CreatedDate);
                }

            }

            return await PagedList<FilamentSummaryDto>.CreateAsync(filamentsBase, pageNumber, pageSize);

        }

        public async Task<Filament> GetFilamentById(Guid id)
        {
            return await _context.Filaments
                    .Where(f => f.Id == id)
                    .Include(f => f.FilamentAdjustments)
                    .Include(f=> f.PrintFilaments)
                    .SingleOrDefaultAsync();
        }

        /// <summary>
        /// Add Filament
        /// </summary>
        /// <param name="filament">The filament to add</param>
        /// <param name="userId">The user adding the filament</param>
        /// <returns></returns>
        public async Task<Filament> AddFilament(AddFilamentDto filament, long userId)
        {
            var newFilament = _mapper.Map<Filament>(filament);

            foreach (var adjustment in newFilament.FilamentAdjustments)
            {
                adjustment.CreatedById = userId;
                adjustment.UpdatedById = userId;
            }

            newFilament.CreatedById = userId;
            newFilament.UpdatedById = userId;

            _context.Filaments.Add(newFilament);
            await _context.SaveChangesAsync();

            var filamentId = newFilament.Id;

            _telemetry.TrackEvent("FilamentAdd");

            return await GetFilamentById(filamentId);
        }

        public async Task<Filament> UpdateFilament(Guid id, FilamentDetailDto dto, long userId)
        {
            var existingFilament = await GetFilamentById(id);

            if (existingFilament == null)
            {
                throw new ArgumentNullException(nameof(id));
            }

            var updatedFilament = _mapper.Map<FilamentDetailDto, Filament>(dto, existingFilament);

            foreach (var adjustment in updatedFilament.FilamentAdjustments)
            {
                adjustment.CreatedById = userId;
                adjustment.UpdatedById = userId;
            }


            // Set UpdatedByIds
            updatedFilament.UpdatedById = userId;

            _context.Entry(updatedFilament).State = EntityState.Modified;

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!FilamentExists(id))
                {
                    throw new DoesNotExistException();
                }
                else
                {
                    throw;
                }
            }

            _telemetry.TrackEvent("FilamentEdit");

            return updatedFilament;
        }

        public async Task<bool> CanUserAccessFilament(long userId, Guid filamentId)
        {
            var filament = await GetFilamentById(filamentId);
            if (filament == null)
            {
                return false;
            }

            // Only the user that created the filament can access it.
            return filament.CreatedById == userId;
        }

        /// <summary>
        /// Delete a Filament if that filament isn't in use by a print.
        /// </summary>
        /// <param name="filamentId"></param>
        /// <returns></returns>
        public async Task DeleteFilament(Guid filamentId)
        {
            var filament = await GetFilamentById(filamentId);
            if (filament == null)
            {
                return;
            }

            // Check if any filament is being used.
            if (filament.PrintFilaments.Any())
            {
                throw new FilamentIsInUseException();
            }

            // Remove any adjustments
            if (filament.FilamentAdjustments.Any())
            {
                _context.FilamentAdjustments.RemoveRange(filament.FilamentAdjustments);
            }

            _context.Filaments.Remove(filament);
            await _context.SaveChangesAsync();

            _telemetry.TrackEvent("FilamentDelete");

            return;
        }

        public bool FilamentExists(Guid id)
        {
            return _context.Filaments.Any(f => f.Id == id);
        }

    }
}
