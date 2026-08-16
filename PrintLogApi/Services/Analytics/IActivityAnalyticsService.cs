using PrintLogApi.Models.DTOs.Analytics;

namespace PrintLogApi.Services.Analytics;

public interface IActivityAnalyticsService
{
    Task<ActivityResponse> GetActivity(long userId, AnalyticsFilter filter, CancellationToken ct);
}
