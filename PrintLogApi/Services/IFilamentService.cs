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
