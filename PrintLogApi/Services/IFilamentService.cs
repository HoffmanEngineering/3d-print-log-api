using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PrintLogApi.Enums;
using PrintLogApi.Mcp;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.SortEnums;

namespace PrintLogApi.Services
{
    public interface IFilamentService
    {
        Task<Filament> AddFilament(AddFilamentDto filament, long userId);

        /// <summary>
        /// Read-only, creator-only, paginated filament inventory for the MCP server. Reuses the
        /// existing remaining-weight expression; results are grams and ordered by display name.
        /// </summary>
        Task<McpPage<MaterialInventoryItem>> GetMaterialInventoryForMcp(
            long userId, int page, int pageSize, string material, string color,
            bool includeInactive, CancellationToken ct);

        /// <summary>
        /// Finds the caller's active spools matching a material and/or color, grouped by their exact
        /// (MaterialType, ColorName) pair. Replaces the old sufficiency check, which summed
        /// incompatible materials (PLA + PLA-CF) and colors (Light Blue + Navy) into a single
        /// boolean and so could report a print as printable when it was not.
        /// </summary>
        Task<FindMaterialResult> FindMaterialForMcp(
            long userId, string material, string color, double? requiredGrams, CancellationToken ct);

        /// <summary>
        /// Remaining weight (grams) for one of the caller's materials, using the same remaining
        /// expression as <see cref="GetMaterialInventoryForMcp"/>. Returns 0 when the material has no
        /// nominal weight or does not belong to <paramref name="userId"/>.
        /// </summary>
        Task<double> GetRemainingGramsForMcp(long userId, Guid materialId, CancellationToken ct);

        /// <summary>
        /// Creates a material for the MCP write surface. The category must exist (no silent fallback
        /// to the default), density must be positive, and diameter is required for diameter-tracking
        /// categories. Reuses the existing measurement-fill logic. Invalidates the user cache.
        /// </summary>
        Task<MaterialInventoryItem> AddMaterialForMcp(
            long userId, string displayName, string materialType, string materialCategoryNickname,
            double densityGramPerCubicCm, double? diameterMm, McpMeasurementSource source,
            double initialAmount, string brand, string colorName, string colorHex,
            string storageLocation, bool isActive, CancellationToken ct);
        Task<bool> CanUserAccessFilament(long userId, Guid filamentId);
        Task<bool> CanUserAccessAllFilaments(long userId, IEnumerable<Guid> filamentIds);
        Task DeleteFilament(Guid filamentId);
        bool FilamentExists(Guid id);
        Task<string[]> GetFilamentBrands(long userId);
        Task<Filament> GetFilamentById(Guid id);
        Task<string[]> GetFilamentPurchaseLocations(long userId);
        Task<string[]> GetFilamentStorageLocations(long userId);
        Task<PagedList<FilamentSummaryDto>> GetFilamentSummaryForUser(long userId, SortDirection sortDirection, FilamentSummarySortColumn sortColumn, int pageNumber, int pageSize, string searchText, string filterByMaterialCategoryNickname, string filterByStorageLocation, bool? includeInactive, bool? showFavoritesOnly, bool? showLoadedFilamentOnly, List<ColorPatternType>? colorPatterns = null, List<FilamentFinishType>? finishTypes = null, List<FilamentEffect>? effects = null);
        Task<Filament> UpdateFilament(Guid id, FilamentDetailDto dto, long userId);
    }
}
