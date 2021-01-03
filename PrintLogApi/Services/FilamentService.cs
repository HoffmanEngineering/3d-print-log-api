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
            int pageSize)
        {
            var filamentsBase = _context.Filaments
                .Where(f => f.CreatedById == userId)
                .ProjectTo<FilamentSummaryDto>(_mapper.ConfigurationProvider)
                .AsNoTracking();

            if (sortColumn == FilamentSummarySortColumn.DisplayName)
            {
                if (sortDirection == SortDirection.Asc)
                {
                    filamentsBase = filamentsBase.OrderBy(f => f.DisplayName).ThenBy(f => f.CreatedDate);
                } else
                {
                    filamentsBase = filamentsBase.OrderByDescending(f => f.DisplayName).ThenByDescending(f => f.CreatedDate);
                }
            }

            return await PagedList<FilamentSummaryDto>.CreateAsync(filamentsBase, pageNumber, pageSize);

        }

        public async Task<Filament> GetFilamentById(Guid id)
        {
            return await _context.Filaments.Where(f => f.Id == id).SingleOrDefaultAsync();
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

        public bool FilamentExists(Guid id)
        {
            return _context.Filaments.Any(f => f.Id == id);
        }

    }
}
