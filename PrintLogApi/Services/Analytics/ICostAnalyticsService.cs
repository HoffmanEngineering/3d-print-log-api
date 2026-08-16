using PrintLogApi.Models.DTOs.Analytics;

namespace PrintLogApi.Services.Analytics;

public interface ICostAnalyticsService
{
    Task<CostsResponse> GetCosts(long userId, AnalyticsFilter filter, CancellationToken ct);
}
