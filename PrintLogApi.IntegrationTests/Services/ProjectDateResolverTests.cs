using PrintLogApi.Services;
using Xunit;
using static PrintLogApi.Services.ProjectDateResolver;

namespace PrintLogApi.IntegrationTests.Services;

public class ProjectDateResolverTests
{
    private static readonly DateTime Created = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Unspecified);

    private static PrintDates Print(string start, int? actual = null, int? estimated = null) =>
        new(DateTimeOffset.Parse(start), actual, estimated);

    [Fact]
    public void NoPrints_StartFallsBackToCreatedDate_FinishIsNull()
    {
        var (start, finish) = Resolve(null, null, Created, []);
        Assert.Equal(new DateOnly(2026, 1, 5), start);
        Assert.Null(finish);
    }

    [Fact]
    public void CreatedDateWithUnspecifiedKind_IsTreatedAsUtc()
    {
        // A local-time reinterpretation would shift this off 2026-01-05.
        var created = new DateTime(2026, 1, 5, 2, 0, 0, DateTimeKind.Unspecified);
        var (start, _) = Resolve(null, null, created, []);
        Assert.Equal(new DateOnly(2026, 1, 5), start);
    }

    [Fact]
    public void AllPrintStartDatesNull_FallsBackToCreatedDate()
    {
        var prints = new[] { new PrintDates(null, 3600, null), new PrintDates(null, null, 60) };
        var (start, finish) = Resolve(null, null, Created, prints);
        Assert.Equal(new DateOnly(2026, 1, 5), start);
        Assert.Null(finish);
    }

    [Fact]
    public void NullStartDates_AreIgnoredAlongsideRealOnes()
    {
        var prints = new[] { new PrintDates(null, 3600, null), Print("2026-03-02T10:00:00Z", 3600) };
        var (start, finish) = Resolve(null, null, Created, prints);
        Assert.Equal(new DateOnly(2026, 3, 2), start);
        Assert.Equal(new DateOnly(2026, 3, 2), finish);
    }

    [Fact]
    public void DerivesEarliestStartAndLatestFinish()
    {
        var prints = new[]
        {
            Print("2026-03-02T10:00:00Z", 3600),
            Print("2026-03-05T10:00:00Z", 3600),
        };
        var (start, finish) = Resolve(null, null, Created, prints);
        Assert.Equal(new DateOnly(2026, 3, 2), start);
        Assert.Equal(new DateOnly(2026, 3, 5), finish);
    }

    [Fact]
    public void FinishUsesStartPlusDuration_NotMaxStart()
    {
        // The earlier print runs 4 days; the later one runs an hour.
        var prints = new[]
        {
            Print("2026-03-02T10:00:00Z", 4 * 24 * 3600),
            Print("2026-03-05T10:00:00Z", 3600),
        };
        var (_, finish) = Resolve(null, null, Created, prints);
        Assert.Equal(new DateOnly(2026, 3, 6), finish);
    }

    [Fact]
    public void DurationFallsBackActualThenEstimatedThenZero()
    {
        Assert.Equal(new DateOnly(2026, 3, 3),
            Resolve(null, null, Created, [Print("2026-03-02T23:00:00Z", actual: 7200)]).Finish);

        Assert.Equal(new DateOnly(2026, 3, 3),
            Resolve(null, null, Created, [Print("2026-03-02T23:00:00Z", actual: 0, estimated: 7200)]).Finish);

        Assert.Equal(new DateOnly(2026, 3, 2),
            Resolve(null, null, Created, [Print("2026-03-02T23:00:00Z")]).Finish);
    }

    [Fact]
    public void OverridesWinIndependently()
    {
        var prints = new[] { Print("2026-03-02T10:00:00Z", 3600) };
        var pinnedStart = new DateOnly(2026, 1, 1);
        var pinnedFinish = new DateOnly(2026, 12, 31);

        Assert.Equal(pinnedStart, Resolve(pinnedStart, null, Created, prints).Start);
        Assert.Equal(new DateOnly(2026, 3, 2), Resolve(pinnedStart, null, Created, prints).Finish);

        Assert.Equal(new DateOnly(2026, 3, 2), Resolve(null, pinnedFinish, Created, prints).Start);
        Assert.Equal(pinnedFinish, Resolve(null, pinnedFinish, Created, prints).Finish);
    }

    [Fact]
    public void ReducesInstantsToUtcCivilDate()
    {
        // 23:30-07:00 is 06:30Z the NEXT day.
        var prints = new[] { Print("2026-03-02T23:30:00-07:00") };
        var (start, _) = Resolve(null, null, Created, prints);
        Assert.Equal(new DateOnly(2026, 3, 3), start);
    }

    [Fact]
    public void HugeDurationSaturatesInsteadOfThrowing()
    {
        var prints = new[] { new PrintDates(DateTimeOffset.MaxValue.AddDays(-1), int.MaxValue, null) };
        var ex = Record.Exception(() => Resolve(null, null, Created, prints));
        Assert.Null(ex);
        Assert.Equal(DateOnly.FromDateTime(DateTimeOffset.MaxValue.UtcDateTime),
            Resolve(null, null, Created, prints).Finish);
    }

    [Fact]
    public void ResolveStartInstant_PrefersOverrideThenPrintThenCreated()
    {
        var earliest = DateTimeOffset.Parse("2026-03-02T10:00:00Z");

        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            ResolveStartInstant(new DateOnly(2026, 1, 1), earliest, Created));
        Assert.Equal(earliest, ResolveStartInstant(null, earliest, Created));
        Assert.Equal(DateTimeOffset.Parse("2026-01-05T00:00:00Z"),
            ResolveStartInstant(null, null, Created));
    }
}
