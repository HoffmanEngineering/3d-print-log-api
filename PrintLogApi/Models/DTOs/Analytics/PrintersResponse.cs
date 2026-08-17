using System.ComponentModel;

namespace PrintLogApi.Models.DTOs.Analytics;

/// <summary>
/// One printer's figures for the window. IsIdle is true when the printer produced nothing in
/// range — returned as a row rather than omitted, because "this printer did nothing" is the
/// most actionable line on the tab and a missing row says it silently.
/// </summary>
public sealed record PrinterRow(
    long PrinterId,
    string Name,
    bool IsIdle,
    int PrintCount,
    double? SuccessRatePercent,
    long PrintTimeSeconds,
    long MaterialMg,
    double? AvgDurationSeconds,
    decimal? Cost,
    decimal? MaintenanceCost,
    double? UtilizationPercent,
    double? CostPerPrintHour);

/// <summary>Keyed by printer id as a string so it serializes as a JSON object, not an array.</summary>
public sealed record PrinterSeriesBucket(
    int Index, DateOnly LocalStart, IReadOnlyDictionary<string, long> PrintSecondsByPrinterId);

public sealed record MaintenanceEvent(
    string Id, long PrinterId, DateOnly Date, string? Category, string? Description, decimal? Cost);

// [ImmutableObject(true)] is read by HybridCache: it permits the cached instance to be
// shared from L1 rather than deserialized per hit. The record'''s own members are init-only,
// but its IReadOnlyList/IReadOnlyDictionary members are backed by mutable collections, so
// this asserts a convention - nothing mutates a cached response - not a guarantee the type
// system enforces. See PagedList<T> for the full rationale.
[ImmutableObject(true)]
public sealed record PrintersResponse(
    DateTimeOffset? From,
    DateTimeOffset? To,
    string TimeZone,
    string Granularity,
    string Currency,
    IReadOnlyList<PrinterRow> Printers,
    IReadOnlyList<PrinterSeriesBucket> TimeSeries,
    Metric FleetUtilizationPercent,
    IReadOnlyList<MaintenanceEvent> Maintenance,
    Coverage Coverage);
