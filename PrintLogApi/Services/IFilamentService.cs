using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.SortEnums;

namespace PrintLogApi.Services
{
    public interface IFilamentService
    {
        Task<Filament> AddFilament(AddFilamentDto filament, long userId);
        Task<bool> CanUserAccessFilament(long userId, Guid filamentId);
        Task DeleteFilament(Guid filamentId);
        bool FilamentExists(Guid id);
        Task<Filament> GetFilamentById(Guid id);
        Task<PagedList<FilamentSummaryDto>> GetFilamentSummaryForUser(long userId, SortDirection sortDirection, FilamentSummarySortColumn sortColumn, int pageNumber, int pageSize, string searchText, bool? includeInactive, bool? showFavoritesOnly, bool? showLoadedFilamentOnly);
        Task<Filament> UpdateFilament(Guid id, FilamentDetailDto dto, long userId);
    }
}
