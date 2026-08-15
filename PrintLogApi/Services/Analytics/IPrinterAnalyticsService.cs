#nullable enable

using System.Threading;
using System.Threading.Tasks;
using PrintLogApi.Models.DTOs.Analytics;

namespace PrintLogApi.Services.Analytics
{
    public interface IPrinterAnalyticsService
    {
        Task<PrintersResponse> GetPrinters(long userId, AnalyticsFilter filter, CancellationToken ct);
    }
}
