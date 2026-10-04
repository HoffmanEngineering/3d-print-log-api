using System.Globalization;

namespace PrintLogApi.Achievements.Metrics;

/// <summary>
/// The anti-backfill rule every streak and date metric follows: a print counts only when it was
/// logged within 48 hours of starting, and not more than 10 minutes before it (clock skew between
/// a printer host and the server). This also rejects future-dated prints.
/// </summary>
public static class QualifyingPrints
{
    private static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromHours(48);

    public static bool Qualifies(PrintDateRow row)
    {
        if (row.StartDate is not { } start)
        {
            return false;
        }

        // CreatedDate is stamped from DateTime.UtcNow but reads back with an unspecified kind.
        var created = DateTime.SpecifyKind(row.CreatedDate, DateTimeKind.Utc);
        var delay = created - start.UtcDateTime;
        return delay >= -MaxSkew && delay <= MaxDelay;
    }
}

/// <summary>
/// Streak and calendar metrics, computed in memory from the shared date projection. A day is the
/// local calendar date of <c>StartDate</c> in the user's zone; a week is an ISO week (Monday
/// start) in that zone.
/// </summary>
public static class DateMetrics
{
    private const int ComebackGapDays = 30;

    public static IEnumerable<IAchievementMetric> All()
    {
        yield return Local("streak.daily", (starts, today) =>
        {
            var days = starts.Select(s => s.Date).Distinct().Order().ToList();
            return Streak(days, d => d.DayNumber, today.DayNumber);
        });
        yield return Local("streak.weekly", (starts, today) =>
        {
            var weeks = starts.Select(s => WeekNumber(s.Date)).Distinct().Order().ToList();
            return Streak(weeks, w => w, WeekNumber(today));
        });
        yield return Local("day.maxPrints", (starts, _) =>
            MetricValue.Count(starts.Count == 0 ? 0 : starts.GroupBy(s => s.Date).Max(g => g.Count())));
        yield return Local("day.comeback", (starts, _) =>
        {
            var days = starts.Select(s => s.Date.DayNumber).Distinct().Order().ToList();
            var cameBack = days.Zip(days.Skip(1)).Any(pair => pair.Second - pair.First >= ComebackGapDays);
            return MetricValue.Count(cameBack ? 1 : 0);
        });
        yield return Local("day.nightOwl", (starts, _) =>
            MetricValue.Count(starts.Any(s => s.Hour < 4) ? 1 : 0));
        yield return Local("day.newYear", (starts, _) =>
            MetricValue.Count(starts.Any(s => s.Date is { Month: 1, Day: 1 }) ? 1 : 0));
    }

    /// <summary>A qualifying print's local start: its calendar date and hour.</summary>
    private readonly record struct LocalStart(DateOnly Date, int Hour);

    private static DelegateMetric Local(string key, Func<IReadOnlyList<LocalStart>, DateOnly, MetricValue> compute) =>
        new(key, async (ctx, ct) =>
        {
            var rows = await ctx.GetPrintDatesAsync(ct);
            var starts = rows
                .Where(QualifyingPrints.Qualifies)
                .Select(r => TimeZoneInfo.ConvertTime(r.StartDate!.Value, ctx.Zone))
                .Select(local => new LocalStart(DateOnly.FromDateTime(local.DateTime), local.Hour))
                .ToList();
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(ctx.NowUtc, DateTimeKind.Utc), ctx.Zone));
            return compute(starts, today);
        });

    /// <summary>A running index of ISO weeks: consecutive weeks differ by exactly one.</summary>
    private static int WeekNumber(DateOnly date)
    {
        var monday = ISOWeek.ToDateTime(ISOWeek.GetYear(date.ToDateTime(TimeOnly.MinValue)),
            ISOWeek.GetWeekOfYear(date.ToDateTime(TimeOnly.MinValue)), DayOfWeek.Monday);
        return DateOnly.FromDateTime(monday).DayNumber / 7;
    }

    /// <summary>
    /// Longest run of consecutive units, and the run that is still alive: one ending in the
    /// current unit or the one before it (a streak survives until the next unit ends).
    /// </summary>
    private static MetricValue Streak<T>(IReadOnlyList<T> sortedDistinct, Func<T, int> index, int current)
    {
        if (sortedDistinct.Count == 0)
        {
            return new MetricValue(0, 0);
        }

        int best = 1, run = 1;
        for (var i = 1; i < sortedDistinct.Count; i++)
        {
            run = index(sortedDistinct[i]) - index(sortedDistinct[i - 1]) == 1 ? run + 1 : 1;
            best = Math.Max(best, run);
        }

        var last = index(sortedDistinct[^1]);
        var alive = current - last is 0 or 1 ? run : 0;
        return new MetricValue(best, alive);
    }
}
