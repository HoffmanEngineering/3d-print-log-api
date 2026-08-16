using System.ComponentModel;

namespace PrintLogApi.Models.DTOs.Analytics;

public sealed record StatusCount(string Status, int Count);

public sealed record SeriesBucket(int Index, DateOnly LocalStart, IReadOnlyDictionary<string, int> CountsByStatus);

/// <summary>A pointer to the entity behind a headline, so the UI can link to it.</summary>
public sealed record HighlightRef(string? Id, string? Label, double? Value, string Unit);

public sealed record OverviewHighlights(
    HighlightRef? MostUsedPrinter,
    HighlightRef? MostUsedMaterial,
    HighlightRef? LongestPrint,
    HighlightRef? PriciestPrint);

public sealed record OverviewTiles(
    Metric PrintCount,
    Metric SuccessRatePercent,
    Metric FilamentGrams,
    Metric PrintTimeSeconds,
    MoneyMetric TotalCost,
    Metric AvgPrintTimeSeconds);

// [ImmutableObject(true)] is read by HybridCache: it permits the cached instance to be
// shared from L1 rather than deserialized per hit. Truthful here - this is a positional
// record with init-only members. See PagedList<T> for the full rationale.
[ImmutableObject(true)]
public sealed record OverviewResponse(
    DateTimeOffset? From,
    DateTimeOffset? To,
    string TimeZone,
    string Granularity,
    OverviewTiles Tiles,
    IReadOnlyList<StatusCount> StatusBreakdown,
    IReadOnlyList<SeriesBucket> Series,
    OverviewHighlights Highlights);
