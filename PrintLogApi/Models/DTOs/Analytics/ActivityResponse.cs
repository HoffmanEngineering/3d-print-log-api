using System.ComponentModel;
using PrintLogApi.Services.Analytics;

namespace PrintLogApi.Models.DTOs.Analytics;

/// <summary>
/// One period, carrying EVERY metric the tab's toggle can show. Four metrics in one payload
/// rather than four requests: the toggle is instant, and the four numbers are guaranteed to
/// describe the same set of prints. Cost is null when the cost row cap was exceeded.
/// </summary>
public sealed record ActivitySeriesBucket(
    int Index, DateOnly LocalStart, int Count, long DurationSeconds, long MaterialMg, decimal? Cost);

public sealed record CalendarDay(DateOnly Date, int Count);

/// <summary>
/// The Activity tab payload. CalendarFrom/CalendarTo carry the window the calendar actually
/// covers, which may be narrower than the filter's, so the UI never has to infer it.
/// </summary>
// [ImmutableObject(true)] is read by HybridCache: it permits the cached instance to be
// shared from L1 rather than deserialized per hit. The record'''s own members are init-only,
// but its IReadOnlyList/IReadOnlyDictionary members are backed by mutable collections, so
// this asserts a convention - nothing mutates a cached response - not a guarantee the type
// system enforces. See PagedList<T> for the full rationale.
[ImmutableObject(true)]
public sealed record ActivityResponse(
    DateTimeOffset? From,
    DateTimeOffset? To,
    string TimeZone,
    string Granularity,
    string? Currency,
    IReadOnlyList<ActivitySeriesBucket> Series,
    IReadOnlyList<CalendarDay> Calendar,
    DateOnly? CalendarFrom,
    DateOnly? CalendarTo,
    StreakSummary Streaks,
    IReadOnlyList<HistogramBucket> DurationHistogram,
    IReadOnlyList<MatrixCell> StartTimeMatrix,
    Coverage Coverage);
