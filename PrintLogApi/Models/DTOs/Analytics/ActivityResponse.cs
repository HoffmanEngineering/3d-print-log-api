#nullable enable

using System;
using System.Collections.Generic;
using PrintLogApi.Services.Analytics;

namespace PrintLogApi.Models.DTOs.Analytics
{
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
    public sealed record ActivityResponse(
        DateTimeOffset? From,
        DateTimeOffset? To,
        string TimeZone,
        string Granularity,
        string Currency,
        IReadOnlyList<ActivitySeriesBucket> Series,
        IReadOnlyList<CalendarDay> Calendar,
        DateOnly? CalendarFrom,
        DateOnly? CalendarTo,
        StreakSummary Streaks,
        IReadOnlyList<HistogramBucket> DurationHistogram,
        IReadOnlyList<MatrixCell> StartTimeMatrix,
        Coverage Coverage);
}
