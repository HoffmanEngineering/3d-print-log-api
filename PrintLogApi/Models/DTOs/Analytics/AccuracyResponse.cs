using System.ComponentModel;
using PrintLogApi.Services.Analytics;

namespace PrintLogApi.Models.DTOs.Analytics;

public sealed record AccuracyGroup(
    string Scope, string Key, string? Label,
    double? MedianRatio, int SampleSize, bool SuppressedForSmallSample);

public sealed record AccuracyTrendBucket(
    int Index, DateOnly LocalStart, double? MedianRatio, int SampleSize);

/// <summary>
/// Structured facts, never a sentence. The client composes "your Ender 3 runs about 18%
/// longer than estimated" — plain language belongs where units and translation already live.
/// </summary>
public sealed record AccuracyCallout(
    string Scope, string Key, string? Label, string Dimension, double MedianRatio, int SampleSize);

/// <summary>
/// The shape NEVER varies by viewport. The phone renders by-printer bars instead of the
/// scatter from this same payload; a viewport-dependent response would break caching and
/// typed clients (spec §11).
/// </summary>
// [ImmutableObject(true)] is read by HybridCache: it permits the cached instance to be
// shared from L1 rather than deserialized per hit. Truthful here - this is a positional
// record with init-only members. See PagedList<T> for the full rationale.
[ImmutableObject(true)]
public sealed record AccuracyResponse(
    DateTimeOffset? From,
    DateTimeOffset? To,
    string TimeZone,
    string Granularity,
    Metric TimeAccuracyMedian,
    Metric MaterialAccuracyMedian,
    IReadOnlyList<ScatterBin> TimeScatter,
    IReadOnlyList<AccuracyGroup> ByPrinter,
    IReadOnlyList<AccuracyGroup> ByMaterial,
    IReadOnlyList<AccuracyTrendBucket> BiasTrend,
    IReadOnlyList<AccuracyCallout> Callouts,
    Coverage Coverage);
