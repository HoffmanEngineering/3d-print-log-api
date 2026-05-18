using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Enums;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.SortEnums;
using static PrintLogApi.Services.MeasurementUtilities;

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
            string filterByMaterialCategoryNickname,
            string filterByStorageLocation,
            bool? includeInactive,
            bool? showFavoritesOnly,
            bool? showLoadedFilamentOnly)
        {
            var filament = _context.Filaments
                .Include(f => f.MaterialCategory)
                .Where(f => f.CreatedById == userId);

            // Filter out unloaded-filaments if requested.
            if (showLoadedFilamentOnly.HasValue && showLoadedFilamentOnly.Value == true)
            {
                filament = filament.Where(f => f.PrinterFilaments.Any(pf => !pf.UnloadedDateTime.HasValue));
            }

            if (!string.IsNullOrEmpty(filterByMaterialCategoryNickname))
            {
                filament = filament.Where(f => f.MaterialCategory.Nickname == filterByMaterialCategoryNickname);
            }

            if (!string.IsNullOrEmpty(filterByStorageLocation))
            {
                if (filterByStorageLocation == "__unassigned__")
                    filament = filament.Where(f => f.StorageLocation == null || f.StorageLocation == "");
                else
                    filament = filament.Where(f => f.StorageLocation == filterByStorageLocation);
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
                    filamentsBase = filamentsBase.OrderBy(f => PrintLogContext.fnNaturalSort(f.DisplayName)).ThenBy(f => f.CreatedDate).ThenBy(f => f.Id);
                } else
                {
                    filamentsBase = filamentsBase.OrderByDescending(f => PrintLogContext.fnNaturalSort(f.DisplayName)).ThenByDescending(f => f.CreatedDate).ThenByDescending(f => f.Id);
                }
            } else if (sortColumn == FilamentSummarySortColumn.FilamentRemaining)
            {
                if (sortDirection == SortDirection.Asc)
                {
                    filamentsBase = filamentsBase.OrderBy(f => f.FilamentRemaining).ThenBy(f => f.DisplayName).ThenBy(f => f.CreatedDate).ThenBy(f => f.Id);
                }
                else
                {
                    filamentsBase = filamentsBase.OrderByDescending(f => f.FilamentRemaining).ThenByDescending(f => f.DisplayName).ThenByDescending(f => f.CreatedDate).ThenByDescending(f => f.Id);
                }
            } else if (sortColumn == FilamentSummarySortColumn.MaterialType)
            {
                if (sortDirection == SortDirection.Asc)
                {
                    filamentsBase = filamentsBase.OrderBy(f => f.MaterialType).ThenBy(f => f.DisplayName).ThenBy(f => f.CreatedDate).ThenBy(f => f.Id);
                }
                else
                {
                    filamentsBase = filamentsBase.OrderByDescending(f => f.MaterialType).ThenByDescending(f => f.DisplayName).ThenByDescending(f => f.CreatedDate).ThenByDescending(f => f.Id);
                }

            }
            else if (sortColumn == FilamentSummarySortColumn.Brand)
            {
                if (sortDirection == SortDirection.Asc)
                {
                    filamentsBase = filamentsBase.OrderBy(f => f.Brand).ThenBy(f => f.DisplayName).ThenBy(f => f.CreatedDate).ThenBy(f => f.Id);
                }
                else
                {
                    filamentsBase = filamentsBase.OrderByDescending(f => f.Brand).ThenByDescending(f => f.DisplayName).ThenByDescending(f => f.CreatedDate).ThenByDescending(f => f.Id);
                }

            }
            else if (sortColumn == FilamentSummarySortColumn.Color)
            {
                if (sortDirection == SortDirection.Asc)
                {
                    filamentsBase = filamentsBase.OrderBy(f => f.ColorName).ThenBy(f => f.DisplayName).ThenBy(f => f.CreatedDate).ThenBy(f => f.Id);
                }
                else
                {
                    filamentsBase = filamentsBase.OrderByDescending(f => f.ColorName).ThenByDescending(f => f.DisplayName).ThenByDescending(f => f.CreatedDate).ThenByDescending(f => f.Id);
                }

            }
            else if (sortColumn == FilamentSummarySortColumn.StorageLocation)
            {
                if (sortDirection == SortDirection.Asc)
                {
                    filamentsBase = filamentsBase.OrderBy(f => f.StorageLocation).ThenBy(f => f.DisplayName).ThenBy(f => f.CreatedDate).ThenBy(f => f.Id);
                }
                else
                {
                    filamentsBase = filamentsBase.OrderByDescending(f => f.StorageLocation).ThenByDescending(f => f.DisplayName).ThenByDescending(f => f.CreatedDate).ThenByDescending(f => f.Id);
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
                    .Include(f => f.MaterialCategory)
                    .AsSplitQuery()
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
            // Backward-compat: old clients send ColorHex only — normalize to Colors array
            if ((filament.Colors == null || filament.Colors.Count == 0) && !string.IsNullOrWhiteSpace(filament.ColorHex))
            {
                filament.Colors = new List<string> { filament.ColorHex };
                filament.ColorPattern ??= ColorPatternType.Solid;
                filament.FinishType ??= FilamentFinishType.Standard;
            }

            // Always keep ColorHex in sync with Colors[0]
            if (filament.Colors != null && filament.Colors.Count > 0)
            {
                filament.ColorHex = filament.Colors[0];
                filament.ColorPattern ??= ColorPatternType.Solid;
                filament.FinishType ??= FilamentFinishType.Standard;
            }

            var newFilament = _mapper.Map<Filament>(filament);

            var materialCategory = await _context.MaterialCategories.FirstOrDefaultAsync(f => f.Nickname == newFilament.MaterialCategoryNickname);

            if (materialCategory == null)
            {
                // Todo, throw error?
                materialCategory = await _context.MaterialCategories.FirstOrDefaultAsync(f => f.Nickname == "filament");
            }

            newFilament.MaterialCategory = materialCategory;

            UpdateFilamentMeasurements(newFilament);

            foreach (var adjustment in newFilament.FilamentAdjustments)
            {
                adjustment.CreatedById = userId;
                adjustment.UpdatedById = userId;

                UpdateFilamentAdjustmentMeasurements(adjustment, newFilament);
            }

            newFilament.CreatedById = userId;
            newFilament.UpdatedById = userId;

            _context.Filaments.Add(newFilament);
            await _context.SaveChangesAsync();

            var filamentId = newFilament.Id;

            _telemetry.TrackEvent("FilamentAdd");

            return await GetFilamentById(filamentId);
        }

        private void UpdateFilamentAdjustmentMeasurements(FilamentAdjustment adjustment, Filament filament)
        {
            if (filament is null || !(filament.MaterialDensityGramPerCubicCm >= 0) || (filament.MaterialCategory.HasDiameter && (!filament.DiameterMm.HasValue || !(filament.DiameterMm >= 0))))
            {
                // Skip any filament that doesn't have the required properties to compute.
                return;
            }

            if (adjustment.Source == FilamentAdjustment.SourceMeasurement.Length)
            {
                if (adjustment.LengthInM.HasValue)
                {
                    adjustment.AmountMg = GetAmountMgFromLength(adjustment.LengthInM.Value, filament.DiameterMm.Value, filament.MaterialDensityGramPerCubicCm);
                    adjustment.VolumeMl = GetVolumeInMlFromLengthM(adjustment.LengthInM.Value, filament.DiameterMm.Value);
                }
            }
            else if (adjustment.Source == FilamentAdjustment.SourceMeasurement.Volume)
            {
                if (adjustment.VolumeMl.HasValue)
                {
                    adjustment.AmountMg = GetAmountMgFromVolume(adjustment.VolumeMl.Value, filament.MaterialDensityGramPerCubicCm);

                    if (filament.MaterialCategory.HasDiameter)
                    {
                        adjustment.LengthInM = GetLengthInMetersFromVolume(adjustment.VolumeMl.Value, filament.DiameterMm.Value);
                    } else
                    {
                        adjustment.LengthInM = null;
                    }
                }
            }
            else
            {

                if (adjustment.AmountMg.HasValue)
                {
                    adjustment.VolumeMl = GetVolumeInMlFromAmount(adjustment.AmountMg.Value, filament.MaterialDensityGramPerCubicCm);

                    if (filament.MaterialCategory.HasDiameter)
                    {
                        adjustment.LengthInM = GetLengthInMetersFromAmount(adjustment.AmountMg.Value, filament.DiameterMm.Value, filament.MaterialDensityGramPerCubicCm);
                    }
                    else
                    {
                        adjustment.LengthInM = null;
                    }
                }
            }
        }

        private void UpdateFilamentMeasurements(Filament filament)
        {

            if (filament is null || !(filament.MaterialDensityGramPerCubicCm >= 0) || (filament.MaterialCategory.HasDiameter && (!filament.DiameterMm.HasValue || !(filament.DiameterMm >= 0))))
            {
                // Skip any filament that doesn't have the required properties to compute.
                return;
            }

            if (filament.Source == Filament.SourceMeasurement.Length)
            {
                if (filament.InitialNominalLengthM.HasValue)
                {
                    filament.InitialNominalWeightMg = GetAmountMgFromLength(filament.InitialNominalLengthM.Value, filament.DiameterMm.Value, filament.MaterialDensityGramPerCubicCm);
                    filament.InitialNominalVolumeMl = GetVolumeInMlFromLengthM(filament.InitialNominalLengthM.Value, filament.DiameterMm.Value);
                }
            } else if (filament.Source == Filament.SourceMeasurement.Volume)
            {
                if (filament.InitialNominalVolumeMl.HasValue)
                {
                    filament.InitialNominalWeightMg = GetAmountMgFromVolume(filament.InitialNominalVolumeMl.Value, filament.MaterialDensityGramPerCubicCm);

                    if (filament.MaterialCategory.HasDiameter)
                    {
                        filament.InitialNominalLengthM = GetLengthInMetersFromVolume(filament.InitialNominalVolumeMl.Value, filament.DiameterMm.Value);
                    }
                    else
                    {
                        filament.InitialNominalLengthM = null;
                    }
                }
            } else
            {

                if (filament.InitialNominalWeightMg.HasValue)
                {
                    filament.InitialNominalVolumeMl = GetVolumeInMlFromAmount(filament.InitialNominalWeightMg.Value, filament.MaterialDensityGramPerCubicCm);

                    if (filament.MaterialCategory.HasDiameter)
                    {
                        filament.InitialNominalLengthM = GetLengthInMetersFromAmount(filament.InitialNominalWeightMg.Value, filament.DiameterMm.Value, filament.MaterialDensityGramPerCubicCm);
                    }
                    else
                    {
                        filament.InitialNominalLengthM = null;
                    }
                }
            }
        }

        public async Task<Filament> UpdateFilament(Guid id, FilamentDetailDto dto, long userId)
        {
            // Backward-compat: old clients send ColorHex only — normalize to Colors array
            if ((dto.Colors == null || dto.Colors.Count == 0) && !string.IsNullOrWhiteSpace(dto.ColorHex))
            {
                dto.Colors = new List<string> { dto.ColorHex };
                dto.ColorPattern = ColorPatternType.Solid;
                dto.FinishType = FilamentFinishType.Standard;
            }

            // Always keep ColorHex in sync with Colors[0]
            if (dto.Colors != null && dto.Colors.Count > 0)
            {
                dto.ColorHex = dto.Colors[0];
            }

            var existingFilament = await GetFilamentById(id);

            if (existingFilament == null)
            {
                throw new ArgumentNullException(nameof(id));
            }

            var updatedFilament = _mapper.Map<FilamentDetailDto, Filament>(dto, existingFilament);

            var materialCategory = await _context.MaterialCategories.FirstOrDefaultAsync(f => f.Nickname == updatedFilament.MaterialCategoryNickname);

            if (materialCategory == null)
            {
                // Todo, throw error?
                materialCategory = await _context.MaterialCategories.FirstOrDefaultAsync(f => f.Nickname == "filament");
            }

            updatedFilament.MaterialCategoryNickname = materialCategory.Nickname;
            updatedFilament.MaterialCategory = materialCategory;

            UpdateFilamentMeasurements(updatedFilament);

            foreach (var adjustment in updatedFilament.FilamentAdjustments)
            {
                adjustment.CreatedById = userId;
                adjustment.UpdatedById = userId;

                UpdateFilamentAdjustmentMeasurements(adjustment, updatedFilament);
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

        public async Task<string[]> GetFilamentStorageLocations(long userId)
        {
            return await _context.Filaments
                .Where(f => f.CreatedById == userId)
                .Where(f => f.StorageLocation != null && f.StorageLocation != "" )
                .Select(f => f.StorageLocation)
                .Distinct()
                .OrderBy(s => s)
                .ToArrayAsync();
        }

        public async Task<string[]> GetFilamentPurchaseLocations(long userId)
        {
            return await _context.Filaments
                .Where(f => f.CreatedById == userId)
                .Where(f => f.PurchaseLocation != null && f.PurchaseLocation != "")
                .Select(f => f.PurchaseLocation)
                .Distinct()
                .OrderBy(s => s)
                .ToArrayAsync();
        }

        public async Task<string[]> GetFilamentBrands(long userId)
        {
            return await _context.Filaments
                .Where(f => f.CreatedById == userId)
                .Where(f => f.Brand != null && f.Brand != "")
                .Select(f => f.Brand)
                .Distinct()
                .OrderBy(s => s)
                .ToArrayAsync();
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

        public async Task<bool> CanUserAccessAllFilaments(long userId, IEnumerable<Guid> filamentIds)
        {
            var ids = filamentIds.Distinct().ToList();
            if (ids.Count == 0) return true;

            var accessibleCount = await _context.Filaments
                .CountAsync(f => ids.Contains(f.Id) && f.CreatedById == userId);

            return accessibleCount == ids.Count;
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
