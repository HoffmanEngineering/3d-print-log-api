using PrintLogApi.Services.Analytics;
using Xunit;

namespace PrintLogApi.IntegrationTests.Analytics;

public class IntervalUnionTests
{
    private static readonly DateTimeOffset WindowFrom = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowTo = new(2026, 7, 2, 0, 0, 0, TimeSpan.Zero);

    private static (DateTimeOffset, DateTimeOffset) At(int startHour, int hours) =>
        (WindowFrom.AddHours(startHour), WindowFrom.AddHours(startHour + hours));

    [Fact]
    public void UnionSeconds_DisjointIntervalsAdd()
    {
        var total = IntervalUnion.UnionSeconds(
            new List<(DateTimeOffset, DateTimeOffset)> { At(0, 2), At(5, 3) },
            WindowFrom, WindowTo);

        Assert.Equal(5 * 3600, total);
    }

    [Fact]
    public void UnionSeconds_OverlappingIntervalsAreCountedOnce()
    {
        // 0-4 and 2-6 cover 0-6, not 8 hours. A naive sum is the bug this exists to prevent.
        var total = IntervalUnion.UnionSeconds(
            new List<(DateTimeOffset, DateTimeOffset)> { At(0, 4), At(2, 4) },
            WindowFrom, WindowTo);

        Assert.Equal(6 * 3600, total);
    }

    [Fact]
    public void UnionSeconds_AContainedIntervalAddsNothing()
    {
        var total = IntervalUnion.UnionSeconds(
            new List<(DateTimeOffset, DateTimeOffset)> { At(0, 10), At(3, 2) },
            WindowFrom, WindowTo);

        Assert.Equal(10 * 3600, total);
    }

    [Fact]
    public void UnionSeconds_ClipsToTheWindowSoUtilizationNeverExceedsOneHundredPercent()
    {
        var total = IntervalUnion.UnionSeconds(
            new List<(DateTimeOffset, DateTimeOffset)>
            {
                (WindowFrom.AddDays(-3), WindowTo.AddDays(3)),
            },
            WindowFrom, WindowTo);

        Assert.Equal((long)(WindowTo - WindowFrom).TotalSeconds, total);
    }

    [Fact]
    public void UnionSeconds_IntervalsEntirelyOutsideTheWindowContributeNothing()
    {
        var total = IntervalUnion.UnionSeconds(
            new List<(DateTimeOffset, DateTimeOffset)>
            {
                (WindowFrom.AddDays(-10), WindowFrom.AddDays(-9)),
                (WindowTo.AddDays(9), WindowTo.AddDays(10)),
            },
            WindowFrom, WindowTo);

        Assert.Equal(0, total);
    }

    [Fact]
    public void UnionSeconds_AdjacentIntervalsMergeWithoutDoubleCountingTheBoundary()
    {
        var total = IntervalUnion.UnionSeconds(
            new List<(DateTimeOffset, DateTimeOffset)> { At(0, 2), At(2, 2) },
            WindowFrom, WindowTo);

        Assert.Equal(4 * 3600, total);
    }

    [Fact]
    public void UnionSeconds_ZeroLengthAndInvertedIntervalsAreDropped()
    {
        var total = IntervalUnion.UnionSeconds(
            new List<(DateTimeOffset, DateTimeOffset)>
            {
                At(1, 0),
                (WindowFrom.AddHours(5), WindowFrom.AddHours(3)),
            },
            WindowFrom, WindowTo);

        Assert.Equal(0, total);
    }

    [Fact]
    public void UnionSeconds_EmptyInputIsZero()
    {
        Assert.Equal(0, IntervalUnion.UnionSeconds(
            new List<(DateTimeOffset, DateTimeOffset)>(), WindowFrom, WindowTo));
    }
}
