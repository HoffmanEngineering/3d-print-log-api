using System.ComponentModel;

namespace PrintLogApi.Models.DTOs.Analytics;

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
///
/// <para>MaterialMg is print consumption only. AdjustmentMg (#231) is the net consumption the
/// group's spools recorded as adjustments in the window: filament removed counts positive, filament
/// added back negative, so it can be below zero. Groups rank by MaterialMg + AdjustmentMg.</para>
/// </summary>
public sealed record MaterialGroup(
    string Key, string Label, int PrintCount, long MaterialMg, SwatchDto Swatch, long AdjustmentMg);

/// <summary>
/// MaterialMgByType is print consumption only. AdjustmentMg (#231) is the bucket's separate
/// "adjustments" series: the net consumption recorded as adjustments, signed like
/// <see cref="MaterialGroup.AdjustmentMg"/>, so a bucket where more was added back than removed
/// is negative.
/// </summary>
public sealed record MaterialSeriesBucket(
    int Index, DateOnly LocalStart, IReadOnlyDictionary<string, long> MaterialMgByType, long AdjustmentMg);

/// <summary>
/// RemainingMg follows the canonical FilamentRemaining rule and may be NEGATIVE: that means
/// usage was logged beyond the spool's initial weight, a real data problem the user should
/// be able to see and fix rather than one we quietly clamp away.
///
/// <para>UsedMg is print consumption in the window; AdjustmentMg (#231) is the window's net
/// adjustment consumption, signed like <see cref="MaterialGroup.AdjustmentMg"/>. PercentConsumed
/// and CostConsumed both describe max(0, UsedMg + AdjustmentMg).</para>
/// </summary>
public sealed record SpoolRow(
    string FilamentId, string Label, SwatchDto Swatch,
    long UsedMg, long? RemainingMg, long? InitialMg,
    double? PercentConsumed, decimal? CostConsumed, long AdjustmentMg);

public sealed record RunwayRow(
    string FilamentId, string Label, SwatchDto Swatch,
    double RemainingGrams, double BurnRateGramsPerDay, double? RunwayDays);

// [ImmutableObject(true)] is read by HybridCache: it permits the cached instance to be
// shared from L1 rather than deserialized per hit. The record'''s own members are init-only,
// but its IReadOnlyList/IReadOnlyDictionary members are backed by mutable collections, so
// this asserts a convention - nothing mutates a cached response - not a guarantee the type
// system enforces. See PagedList<T> for the full rationale.
/// <summary>
/// Coverage counts prints. Adjustments (#231) are counted on their own: AdjustmentCount is the
/// adjustment rows the figures include, and RowCapExceeded says the tab was emptied because
/// usage and adjustment rows together exceeded the row cap, which the print coverage cannot
/// say when there are no prints to exclude.
/// </summary>
[ImmutableObject(true)]
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
    Coverage Coverage,
    int AdjustmentCount,
    bool RowCapExceeded);
