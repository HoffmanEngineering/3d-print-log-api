#nullable enable

using System;
using System.Collections.Generic;

namespace PrintLogApi.Models.DTOs.Analytics
{
    /// <summary>
    /// A filament's appearance, sent raw. The UI turns it into CSS or SVG through its shared
    /// swatch descriptor — the server never emits presentation strings.
    /// </summary>
    public sealed record SwatchDto(
        IReadOnlyList<string> Colors, int ColorPattern, int FinishType, IReadOnlyList<int> Effects);

    /// <summary>
    /// PrintCount is DISTINCT prints for a real group, so "most used" ranks by prints rather
    /// than by usage rows (spec §5).
    ///
    /// The synthetic "Other" rollup is the one exception: it sums its members' counts, so a
    /// print spanning two rolled-up groups contributes twice. That is a count of group
    /// appearances, not of prints, and the row is labelled "Other" rather than carrying a claim
    /// about any particular material — but the field means something slightly different there,
    /// and callers ranking or charting it should not treat it as a print count.
    /// </summary>
    public sealed record MaterialGroup(
        string Key, string Label, int PrintCount, long MaterialMg, SwatchDto Swatch);

    public sealed record MaterialSeriesBucket(
        int Index, DateOnly LocalStart, IReadOnlyDictionary<string, long> MaterialMgByType);

    /// <summary>
    /// RemainingMg follows the canonical FilamentRemaining rule and may be NEGATIVE: that means
    /// usage was logged beyond the spool's initial weight, a real data problem the user should
    /// be able to see and fix rather than one we quietly clamp away.
    /// </summary>
    public sealed record SpoolRow(
        string FilamentId, string Label, SwatchDto Swatch,
        long UsedMg, long? RemainingMg, long? InitialMg,
        double? PercentConsumed, decimal? CostConsumed);

    public sealed record RunwayRow(
        string FilamentId, string Label, SwatchDto Swatch,
        double RemainingGrams, double BurnRateGramsPerDay, double? RunwayDays);

    public sealed record MaterialsResponse(
        DateTimeOffset? From,
        DateTimeOffset? To,
        string TimeZone,
        string Granularity,
        string Currency,
        IReadOnlyList<MaterialGroup> ByType,
        IReadOnlyList<MaterialGroup> ByBrand,
        IReadOnlyList<MaterialGroup> ByColor,
        IReadOnlyList<MaterialSeriesBucket> ConsumptionOverTime,
        IReadOnlyList<SpoolRow> TopSpools,
        IReadOnlyList<RunwayRow> Runway,
        Metric WasteGrams,
        MoneyMetric WasteCost,
        Coverage Coverage);
}
