using System.Threading;
using System.Threading.Tasks;
using PrintLogApi.Models.DTOs.Analytics;

namespace PrintLogApi.Services.Analytics
{
    public interface IMaterialAnalyticsService
    {
        Task<MaterialsResponse> GetMaterials(long userId, AnalyticsFilter filter, CancellationToken ct);
    }
}
