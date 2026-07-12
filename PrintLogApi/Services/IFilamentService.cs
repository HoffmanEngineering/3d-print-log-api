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
        /// Sums the remaining weight (mg) across the caller's active inventory, optionally filtered
        /// by material and/or color, via a database aggregate. Used by check_material_sufficiency.
        /// </summary>
        Task<long> GetAvailableMaterialMgForMcp(
            long userId, string material, string color, CancellationToken ct);
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
