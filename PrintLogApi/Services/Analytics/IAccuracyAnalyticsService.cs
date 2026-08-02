using System.Threading;
using System.Threading.Tasks;
using PrintLogApi.Models.DTOs.Analytics;

namespace PrintLogApi.Services.Analytics
{
    public interface IAccuracyAnalyticsService
    {
        Task<AccuracyResponse> GetAccuracy(long userId, AnalyticsFilter filter, CancellationToken ct);
    }
}
