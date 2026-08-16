namespace PrintLogApi.Services.Analytics;

/// <summary>
/// Total wall-clock seconds covered by a set of possibly-overlapping intervals, clipped to a
/// window.
///
/// Utilization has to be a union, not a sum (spec §5). The schema records a start plus a
/// duration and has no non-overlapping job timeline, so overlapping or mis-logged prints
/// would push a summed figure above 100% of the very window it is divided by. Union is
/// bounded by the window by construction.
/// </summary>
public static class IntervalUnion
{
    public static long UnionSeconds(
        IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> intervals,
        DateTimeOffset windowFrom,
        DateTimeOffset windowTo)
    {
        if (windowTo <= windowFrom) return 0;

        var clipped = (intervals ?? Enumerable.Empty<(DateTimeOffset, DateTimeOffset)>())
            .Select(i => (
                Start: i.Start < windowFrom ? windowFrom : i.Start,
                End: i.End > windowTo ? windowTo : i.End))
            .Where(i => i.End > i.Start)
            .OrderBy(i => i.Start)
            .ToList();

        if (clipped.Count == 0) return 0;

        double seconds = 0;
        var mergedStart = clipped[0].Start;
        var mergedEnd = clipped[0].End;

        foreach (var (start, end) in clipped.Skip(1))
        {
            // <= rather than <: an interval starting exactly where the previous ended is
            // contiguous, and closing then reopening would count the boundary instant twice.
            if (start <= mergedEnd)
            {
                if (end > mergedEnd) mergedEnd = end;
                continue;
            }

            seconds += (mergedEnd - mergedStart).TotalSeconds;
            mergedStart = start;
            mergedEnd = end;
        }

        seconds += (mergedEnd - mergedStart).TotalSeconds;
        return (long)Math.Round(seconds, MidpointRounding.AwayFromZero);
    }
}
