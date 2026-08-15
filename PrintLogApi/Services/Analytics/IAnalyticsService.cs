#nullable enable

using System.Threading;
using System.Threading.Tasks;
using PrintLogApi.Models.DTOs.Analytics;

namespace PrintLogApi.Services.Analytics
{
    public interface IAnalyticsService
    {
        Task<OverviewResponse> GetOverview(long userId, AnalyticsFilter filter, CancellationToken ct);
    }
}
