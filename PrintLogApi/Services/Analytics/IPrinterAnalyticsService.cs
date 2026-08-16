using PrintLogApi.Models.DTOs.Analytics;

namespace PrintLogApi.Services.Analytics;

public interface IPrinterAnalyticsService
{
    Task<PrintersResponse> GetPrinters(long userId, AnalyticsFilter filter, CancellationToken ct);
}
