using PrintLogApi.Models.DTOs.Analytics;

namespace PrintLogApi.Services.Analytics;

/// <summary>A half-open [StartUtc, EndUtc) bucket anchored to a civil local date.</summary>
public sealed record TimeBucket(int Index, DateTimeOffset StartUtc, DateTimeOffset EndUtc, DateOnly LocalStart);

/// <summary>
/// Builds local-calendar buckets and their UTC boundaries.
///
/// A single fixed UTC offset cannot do this: any range spanning a DST transition needs a
/// different offset on different dates, so the spring-forward local day is 23 hours and the
/// fall-back day is 25. Boundaries are therefore derived from civil local dates via
/// TimeZoneInfo and converted to UTC, and no SQL date arithmetic depends on an offset.
/// </summary>
public static class TimeBucketer
{
    public static IReadOnlyList<TimeBucket> BuildBuckets(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        TimeZoneInfo zone,
        AnalyticsGranularity granularity,
        DayOfWeek weekStart)
    {
        if (granularity == AnalyticsGranularity.Auto)
            throw new ArgumentException("Resolve Auto before bucketing.", nameof(granularity));
        if (toUtc <= fromUtc) return Array.Empty<TimeBucket>();

        var localFrom = TimeZoneInfo.ConvertTime(fromUtc, zone);
        var cursor = AlignDown(DateOnly.FromDateTime(localFrom.Date), granularity, weekStart);

        var buckets = new List<TimeBucket>();
        var index = 0;

        while (true)
        {
            var next = Advance(cursor, granularity);
            var startUtc = ToUtc(cursor, zone);
            var endUtc = ToUtc(next, zone);

            if (endUtc > fromUtc && startUtc < toUtc)
                buckets.Add(new TimeBucket(index++, startUtc, endUtc, cursor));

            if (startUtc >= toUtc) break;
            cursor = next;
        }

        return buckets;
    }

    /// <summary>Binary search. Returns -1 when the instant falls outside every bucket.</summary>
    public static int IndexOf(IReadOnlyList<TimeBucket> buckets, DateTimeOffset instantUtc)
    {
        int lo = 0, hi = buckets.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            var b = buckets[mid];
            if (instantUtc < b.StartUtc) hi = mid - 1;
            else if (instantUtc >= b.EndUtc) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    public static (DateTimeOffset From, DateTimeOffset To) PreviousWindow(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var span = toUtc - fromUtc;
        return (fromUtc - span, fromUtc);
    }

    private static DateOnly AlignDown(DateOnly d, AnalyticsGranularity g, DayOfWeek weekStart) => g switch
    {
        AnalyticsGranularity.Day => d,
        AnalyticsGranularity.Week => d.AddDays(-(((int)d.DayOfWeek - (int)weekStart + 7) % 7)),
        AnalyticsGranularity.Month => new DateOnly(d.Year, d.Month, 1),
        _ => d,
    };

    private static DateOnly Advance(DateOnly d, AnalyticsGranularity g) => g switch
    {
        AnalyticsGranularity.Day => d.AddDays(1),
        AnalyticsGranularity.Week => d.AddDays(7),
        AnalyticsGranularity.Month => d.AddMonths(1),
        _ => d.AddDays(1),
    };

    /// <summary>
    /// Local midnight → UTC. On a spring-forward date local midnight is never invalid, but
    /// GetUtcOffset is asked for the offset AT that instant so 23/25-hour days fall out
    /// naturally rather than being assumed to be 24.
    /// </summary>
    private static DateTimeOffset ToUtc(DateOnly localDate, TimeZoneInfo zone)
    {
        var naive = localDate.ToDateTime(TimeOnly.MinValue);
        if (zone.IsInvalidTime(naive))
            naive = naive.AddHours(1); // the skipped hour: the day begins at 01:00 local
        var offset = zone.GetUtcOffset(naive);
        return new DateTimeOffset(naive, offset).ToUniversalTime();
    }
}
