using PrintLogApi.Services.Analytics;
using Xunit;

namespace PrintLogApi.IntegrationTests.Analytics;

public class ActivityStatsTests
{
    private static DayCount D(int year, int month, int day, int count = 1) =>
        new(new DateOnly(year, month, day), count);

    [Fact]
    public void Streaks_LongestIsTheLongestRunOfConsecutiveDays()
    {
        var days = new List<DayCount>
        {
            D(2026, 7, 1), D(2026, 7, 2), D(2026, 7, 3),   // run of 3
            D(2026, 7, 10),                                 // gap
            D(2026, 7, 20), D(2026, 7, 21),                 // run of 2
        };

        var s = ActivityStats.Streaks(days, new DateOnly(2026, 7, 25));

        Assert.Equal(3, s.LongestDays);
        Assert.Equal(new DateOnly(2026, 7, 1), s.LongestStart);
        Assert.Equal(new DateOnly(2026, 7, 3), s.LongestEnd);
    }

    [Fact]
    public void Streaks_CurrentCountsARunEndingToday()
    {
        var days = new List<DayCount> { D(2026, 7, 23), D(2026, 7, 24), D(2026, 7, 25) };

        Assert.Equal(3, ActivityStats.Streaks(days, new DateOnly(2026, 7, 25)).CurrentDays);
    }

    [Fact]
    public void Streaks_CurrentCountsARunEndingYesterdayBecauseTodayIsStillInProgress()
    {
        var days = new List<DayCount> { D(2026, 7, 23), D(2026, 7, 24) };

        Assert.Equal(2, ActivityStats.Streaks(days, new DateOnly(2026, 7, 25)).CurrentDays);
    }

    [Fact]
    public void Streaks_CurrentIsZeroWhenTheLastPrintIsOlderThanYesterday()
    {
        // The whole point: a run that ended in March is not a streak you are "on".
        var days = new List<DayCount> { D(2026, 3, 1), D(2026, 3, 2), D(2026, 3, 3) };

        var s = ActivityStats.Streaks(days, new DateOnly(2026, 7, 25));

        Assert.Equal(0, s.CurrentDays);
        Assert.Equal(3, s.LongestDays);
    }

    [Fact]
    public void Streaks_BusiestDayAndWeekdayResolveTiesDeterministically()
    {
        var days = new List<DayCount>
        {
            D(2026, 7, 6, 5),   // Monday
            D(2026, 7, 13, 5),  // Monday, same count — earliest date wins
            D(2026, 7, 7, 1),   // Tuesday
        };

        var s = ActivityStats.Streaks(days, new DateOnly(2026, 7, 25));

        Assert.Equal(new DateOnly(2026, 7, 6), s.BusiestDate);
        Assert.Equal(5, s.BusiestDateCount);
        Assert.Equal((int)DayOfWeek.Monday, s.BusiestWeekday);
        Assert.Equal(10, s.BusiestWeekdayCount);
    }

    [Fact]
    public void Streaks_EmptyInputIsAllZeroAndNeverThrows()
    {
        var s = ActivityStats.Streaks(new List<DayCount>(), new DateOnly(2026, 7, 25));

        Assert.Equal(0, s.CurrentDays);
        Assert.Equal(0, s.LongestDays);
        Assert.Null(s.BusiestDate);
        Assert.Null(s.BusiestWeekday);
    }

    [Fact]
    public void DurationHistogram_AlwaysReturnsAllEightBucketsInOrder()
    {
        var buckets = ActivityStats.DurationHistogram(Array.Empty<(int, int)>());

        Assert.Equal(
            new[] { "<30m", "30m–1h", "1–2h", "2–4h", "4–8h", "8–12h", "12–24h", "24h+" },
            buckets.Select(b => b.Label).ToArray());
        Assert.All(buckets, b => Assert.Equal(0, b.Count));
    }

    [Fact]
    public void DurationHistogram_EdgesAreHalfOpenSoABoundaryLandsInTheUpperBucket()
    {
        // 1800s is exactly 30 minutes: it belongs to "30m–1h", not "<30m".
        var buckets = ActivityStats.DurationHistogram(
            new[] { (1799, 1), (1800, 1), (3600, 1), (86400, 1) });

        Assert.Equal(1, buckets.Single(b => b.Label == "<30m").Count);
        Assert.Equal(1, buckets.Single(b => b.Label == "30m–1h").Count);
        Assert.Equal(1, buckets.Single(b => b.Label == "1–2h").Count);
        Assert.Equal(1, buckets.Single(b => b.Label == "24h+").Count);
    }

    [Fact]
    public void DurationHistogram_CountsEachSampleByItsWeightNotOnce()
    {
        // The weight IS the print count: the service groups prints by duration before
        // calling this, so a pair of (7200s, 5) means five two-hour prints, not one.
        var buckets = ActivityStats.DurationHistogram(new[] { (7200, 5), (7200, 2) });

        Assert.Equal(7, buckets.Single(b => b.Label == "2–4h").Count);
    }

    [Fact]
    public void DurationHistogram_IgnoresANonPositiveWeight()
    {
        var buckets = ActivityStats.DurationHistogram(new[] { (7200, 0), (7200, -3) });

        Assert.All(buckets, b => Assert.Equal(0, b.Count));
    }

    [Fact]
    public void StartTimeMatrix_EmitsAllOneHundredAndSixtyEightCells()
    {
        var cells = ActivityStats.StartTimeMatrix(
            new[] { (Weekday: 1, Hour: 9, Count: 2), (Weekday: 1, Hour: 9, Count: 3) });

        Assert.Equal(168, cells.Count);
        Assert.Equal(5, cells.Single(c => c.Weekday == 1 && c.Hour == 9).Count);
        Assert.Equal(0, cells.Single(c => c.Weekday == 0 && c.Hour == 0).Count);
    }

    [Fact]
    public void StartTimeMatrix_IgnoresOutOfRangeObservationsRatherThanThrowing()
    {
        var cells = ActivityStats.StartTimeMatrix(
            new[] { (Weekday: 9, Hour: 9, Count: 1), (Weekday: 1, Hour: 99, Count: 1) });

        Assert.Equal(168, cells.Count);
        Assert.All(cells, c => Assert.Equal(0, c.Count));
    }
}
