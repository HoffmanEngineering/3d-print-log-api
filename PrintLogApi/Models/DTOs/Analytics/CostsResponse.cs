using System;
using System.Collections.Generic;
using PrintLogApi.Services.Analytics;

namespace PrintLogApi.Models.DTOs.Analytics
{
    /// <summary>Components stay separate: they fail independently and the chart stacks them.</summary>
    public sealed record CostSeriesBucket(
        int Index, DateOnly LocalStart, decimal? Filament, decimal? Electricity, decimal? Maintenance);

    public sealed record CostGroup(string Key, string Label, decimal Amount, int PrintCount);

    public sealed record PrintCostRef(long PrintId, string? Title, DateOnly? Date, decimal Amount);

    /// <summary>
    /// The Costs tab payload. CostOfFailureSharePercent is null when total spend is 0 — a share
    /// of nothing is undefined, not 0%, and 0% would read as "no failures".
    /// </summary>
    public sealed record CostsResponse(
        DateTimeOffset? From,
        DateTimeOffset? To,
        string TimeZone,
        string Granularity,
        string Currency,
        MoneyMetric TotalSpend,
        MoneyMetric FilamentSpend,
        MoneyMetric ElectricitySpend,
        MoneyMetric MaintenanceSpend,
        IReadOnlyList<CostSeriesBucket> SpendOverTime,
        IReadOnlyList<HistogramBucket> CostPerPrint,
        IReadOnlyList<CostGroup> ByMaterialType,
        IReadOnlyList<CostGroup> ByBrand,
        MoneyMetric CostOfFailure,
        double? CostOfFailureSharePercent,
        IReadOnlyList<PrintCostRef> MostExpensive,
        IReadOnlyList<PrintCostRef> LeastExpensive,
        Coverage Coverage);
}
