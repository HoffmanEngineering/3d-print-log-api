using System.ComponentModel;
using PrintLogApi.Services.Analytics;

namespace PrintLogApi.Models.DTOs.Analytics;

/// <summary>Components stay separate: they fail independently and the chart stacks them.</summary>
public sealed record CostSeriesBucket(
    int Index, DateOnly LocalStart, decimal? Filament, decimal? Electricity, decimal? Maintenance);

public sealed record CostGroup(string Key, string Label, decimal Amount, int PrintCount);

public sealed record PrintCostRef(long PrintId, string? Title, DateOnly? Date, decimal Amount);

/// <summary>
/// The Costs tab payload. CostOfFailureSharePercent is null when total spend is 0 — a share
/// of nothing is undefined, not 0%, and 0% would read as "no failures".
/// </summary>
// [ImmutableObject(true)] is read by HybridCache: it permits the cached instance to be
// shared from L1 rather than deserialized per hit. Truthful here - this is a positional
// record with init-only members. See PagedList<T> for the full rationale.
[ImmutableObject(true)]
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
