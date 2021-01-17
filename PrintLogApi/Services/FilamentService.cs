using System;
using System.Collections.Generic;
using System.Linq;
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
            bool? includeInactive)
        {
            var filamentsBase = _context.Filaments
                .Where(f => f.CreatedById == userId)
                .ProjectTo<FilamentSummaryDto>(_mapper.ConfigurationProvider)
                .AsNoTracking();

            if (!string.IsNullOrWhiteSpace(searchText))
            {
                filamentsBase = filamentsBase.Where(f => f.DisplayName.Contains(searchText) || f.Brand.Contains(searchText) || f.ColorName.Contains(searchText) || f.Notes.Contains(searchText));
            }

            // Filter out inactives unless specified
            if (!includeInactive.HasValue || includeInactive.Value == false)
            {
                filamentsBase = filamentsBase.Where(f => f.IsActive == true);
            } 

            if (sortColumn == FilamentSummarySortColumn.DisplayName)
            {
                if (sortDirection == SortDirection.Asc)
                {
                    filamentsBase = filamentsBase.OrderBy(f => f.DisplayName).ThenBy(f => f.CreatedDate);
                } else
                {
                    filamentsBase = filamentsBase.OrderByDescending(f => f.DisplayName).ThenByDescending(f => f.CreatedDate);
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

            return await PagedList<FilamentSummaryDto>.CreateAsync(filamentsBase, pageNumber, pageSize);

        }

        public async Task<Filament> GetFilamentById(Guid id)
        {
            return await _context.Filaments
                    .Where(f => f.Id == id)
                    .Include(f => f.FilamentAdjustments)
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

        public bool FilamentExists(Guid id)
        {
            return _context.Filaments.Any(f => f.Id == id);
        }

    }
}
