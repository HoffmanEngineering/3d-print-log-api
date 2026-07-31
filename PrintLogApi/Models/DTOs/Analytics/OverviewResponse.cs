using System;
using System.Collections.Generic;

namespace PrintLogApi.Models.DTOs.Analytics
{
    public sealed record StatusCount(string Status, int Count);

    public sealed record SeriesBucket(int Index, DateOnly LocalStart, IReadOnlyDictionary<string, int> CountsByStatus);

    /// <summary>A pointer to the entity behind a headline, so the UI can link to it.</summary>
    public sealed record HighlightRef(string Id, string Label, double? Value, string Unit);

    public sealed record OverviewHighlights(
        HighlightRef MostUsedPrinter,
        HighlightRef MostUsedMaterial,
        HighlightRef LongestPrint,
        HighlightRef PriciestPrint);

    public sealed record OverviewTiles(
        Metric PrintCount,
        Metric SuccessRatePercent,
        Metric FilamentGrams,
        Metric PrintTimeSeconds,
        MoneyMetric TotalCost,
        Metric AvgPrintTimeSeconds);

    public sealed record OverviewResponse(
        DateTimeOffset? From,
        DateTimeOffset? To,
        string TimeZone,
        string Granularity,
        OverviewTiles Tiles,
        IReadOnlyList<StatusCount> StatusBreakdown,
        IReadOnlyList<SeriesBucket> Series,
        OverviewHighlights Highlights);
}
