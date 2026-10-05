using PrintLogApi.Achievements.Metrics;
using PrintLogApi.Models;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Achievements;

public class DateMetricsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public DateMetricsTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeZoneInfo NewYork = TimeZoneResolver.ResolveOrUtc("America/New_York");

    // Noon UTC on a fixed day, so local-date arithmetic in UTC is unambiguous.
    private static DateTimeOffset Day(int offset) => new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero).AddDays(offset);

    private async Task<(IServiceScope Scope, PrintLogContext Db, User User)> ArrangeAsync()
    {
        var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return (scope, db, await AchievementTestData.CreateUserAsync(db));
    }

    /// <summary>A print started at <paramref name="start"/> and logged shortly after it.</summary>
    private static async Task AddStartedAsync(PrintLogContext db, User user, DateTimeOffset start, TimeSpan? loggedAfter = null)
    {
        var print = await AchievementTestData.AddPrintAsync(db, user, p => p.StartDate = start);
        await AchievementTestData.SetCreatedDateAsync(db, print.Id, (start + (loggedAfter ?? TimeSpan.FromMinutes(5))).UtcDateTime);
    }

    private async Task<MetricValue> MeasureAsync(IServiceScope scope, PrintLogContext db, User user, string key, DateTimeOffset now, TimeZoneInfo? zone = null) =>
        await AchievementMetricTestKit.Context(scope.ServiceProvider, db, user.Id, zone, now.UtcDateTime).MeasureAsync(key, Ct);

    private static PrintDateRow Row(TimeSpan createdMinusStart) =>
        new(Day(0), (Day(0) + createdMinusStart).UtcDateTime, Print.PrintStatus.Success);

    [Fact]
    public void Qualifies_RejectsFutureBeyondSkew()
    {
        Assert.False(QualifyingPrints.Qualifies(Row(TimeSpan.FromMinutes(-11))));
        Assert.True(QualifyingPrints.Qualifies(Row(TimeSpan.FromMinutes(-9))));
    }

    [Fact]
    public void Qualifies_Rejects48hPlus1s()
    {
        Assert.True(QualifyingPrints.Qualifies(Row(TimeSpan.FromHours(48))));
        Assert.False(QualifyingPrints.Qualifies(Row(TimeSpan.FromHours(48) + TimeSpan.FromSeconds(1))));
    }

    [Fact]
    public void Qualifies_RejectsNullStart()
    {
        Assert.False(QualifyingPrints.Qualifies(new PrintDateRow(null, Day(0).UtcDateTime, Print.PrintStatus.Success)));
    }

    [Fact]
    public async Task DailyStreak_LongestAndCurrent()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        foreach (var d in new[] { 1, 2, 3, 5, 6, 7, 8, 9 })
        {
            await AddStartedAsync(db, user, Day(d));
        }

        var value = await MeasureAsync(scope, db, user, "streak.daily", Day(10));

        Assert.Equal(new MetricValue(5, 5), value);
    }

    [Fact]
    public async Task DailyStreak_CurrentZeroAfterMissedDay()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        foreach (var d in new[] { 1, 2, 3 })
        {
            await AddStartedAsync(db, user, Day(d));
        }

        Assert.Equal(new MetricValue(3, 3), await MeasureAsync(scope, db, user, "streak.daily", Day(4)));
        Assert.Equal(new MetricValue(3, 0), await MeasureAsync(scope, db, user, "streak.daily", Day(5)));
    }

    [Fact]
    public async Task DailyStreak_UsesLocalDate_AcrossMidnight()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        // 23:30 and 00:30 New York time: one UTC date (04:30Z and 05:30Z), two local dates.
        await AddStartedAsync(db, user, new DateTimeOffset(2026, 6, 10, 3, 30, 0, TimeSpan.Zero));
        await AddStartedAsync(db, user, new DateTimeOffset(2026, 6, 10, 4, 30, 0, TimeSpan.Zero));
        var now = new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(2, (await MeasureAsync(scope, db, user, "streak.daily", now, NewYork)).Best);
        Assert.Equal(1, (await MeasureAsync(scope, db, user, "streak.daily", now, TimeZoneInfo.Utc)).Best);
    }

    [Fact]
    public async Task DailyStreak_DstTransition()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        // US clocks spring forward on 2026-03-08. Noon local every day from the 6th to the 10th.
        for (var day = 6; day <= 10; day++)
        {
            var local = new DateTime(2026, 3, day, 12, 0, 0, DateTimeKind.Unspecified);
            await AddStartedAsync(db, user, new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, NewYork)));
        }

        var value = await MeasureAsync(scope, db, user, "streak.daily", new DateTimeOffset(2026, 3, 10, 20, 0, 0, TimeSpan.Zero), NewYork);

        Assert.Equal(new MetricValue(5, 5), value);
    }

    [Fact]
    public async Task WeeklyStreak_IsoWeekBoundary_YearEnd()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        // 2026-W52, 2026-W53 and 2027-W01 are consecutive ISO weeks.
        await AddStartedAsync(db, user, new DateTimeOffset(2026, 12, 21, 12, 0, 0, TimeSpan.Zero));
        await AddStartedAsync(db, user, new DateTimeOffset(2026, 12, 29, 12, 0, 0, TimeSpan.Zero));
        await AddStartedAsync(db, user, new DateTimeOffset(2027, 1, 5, 12, 0, 0, TimeSpan.Zero));

        var value = await MeasureAsync(scope, db, user, "streak.weekly", new DateTimeOffset(2027, 1, 12, 12, 0, 0, TimeSpan.Zero));

        // Week of the 12th is W02: the run ended last week, so it is still current.
        Assert.Equal(new MetricValue(3, 3), value);
        Assert.Equal(new MetricValue(3, 0),
            await MeasureAsync(scope, db, user, "streak.weekly", new DateTimeOffset(2027, 1, 19, 12, 0, 0, TimeSpan.Zero)));
    }

    private static async Task SetTimeZoneAsync(PrintLogContext db, User user, string value)
    {
        db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            UserSettingTypeId = 20,
            Value = value,
            CreatedById = user.Id,
            UpdatedById = user.Id,
        });
        await db.SaveChangesAsync(Ct);
    }

    // A date badge granted in the wrong zone is wrong for good (grants are never revoked): UTC
    // midnight to 4 AM is the evening in the Americas, so a UTC fallback would hand out Night
    // Owl for evening prints. Until a zone is known, every date metric reads zero; saving one
    // raises TimeZoneChanged, which re-evaluates them.
    [Fact]
    public async Task DateMetrics_InvalidTimeZone_AreWithheld()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        await SetTimeZoneAsync(db, user, "Mars/Olympus");
        await AddStartedAsync(db, user, Day(1));
        await AddStartedAsync(db, user, Day(2));

        var ctx = await EvaluationContext.CreateAsync(db, user.Id, Day(2).UtcDateTime,
            scope.ServiceProvider.GetServices<IAchievementMetric>(), Ct);

        Assert.False(ctx.ZoneKnown);
        Assert.Equal(new MetricValue(0, 0), await ctx.MeasureAsync("streak.daily", Ct));
    }

    [Theory]
    [InlineData("streak.daily")]
    [InlineData("streak.weekly")]
    [InlineData("day.maxPrints")]
    [InlineData("day.comeback")]
    [InlineData("day.nightOwl")]
    [InlineData("day.newYear")]
    public async Task DateMetrics_MissingTimeZone_AreWithheld(string key)
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        // Qualifies for every date metric in UTC: a 2 AM start on January 1st, three prints that
        // day, the next two days, and a return after a 40-day gap.
        var newYear = new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);
        foreach (var start in new[] { newYear, newYear.AddHours(1), newYear.AddHours(2), newYear.AddDays(1), newYear.AddDays(2), newYear.AddDays(42) })
        {
            await AddStartedAsync(db, user, start);
        }

        var ctx = await EvaluationContext.CreateAsync(db, user.Id, newYear.AddDays(42).UtcDateTime,
            scope.ServiceProvider.GetServices<IAchievementMetric>(), Ct);

        Assert.False(ctx.ZoneKnown);
        Assert.Equal(new MetricValue(0, 0), await ctx.MeasureAsync(key, Ct));
    }

    [Fact]
    public async Task Streak_SavedTimeZone_IsUsed()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        await SetTimeZoneAsync(db, user, "America/New_York");

        var ctx = await EvaluationContext.CreateAsync(db, user.Id, Day(0).UtcDateTime,
            scope.ServiceProvider.GetServices<IAchievementMetric>(), Ct);

        Assert.True(ctx.ZoneKnown);
        Assert.Equal(NewYork.BaseUtcOffset, ctx.Zone.BaseUtcOffset);
    }

    [Fact]
    public async Task BusyDay_MaxPerLocalDate()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        await AddStartedAsync(db, user, Day(1));
        await AddStartedAsync(db, user, Day(1).AddHours(2));
        await AddStartedAsync(db, user, Day(1).AddHours(3));
        await AddStartedAsync(db, user, Day(2));

        Assert.Equal(new MetricValue(3, 3), await MeasureAsync(scope, db, user, "day.maxPrints", Day(3)));
    }

    [Fact]
    public async Task Comeback_RequiresThirtyDayGap()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        await AddStartedAsync(db, user, Day(0));
        await AddStartedAsync(db, user, Day(29));
        Assert.Equal(0, (await MeasureAsync(scope, db, user, "day.comeback", Day(40))).Best);

        await AddStartedAsync(db, user, Day(59));
        Assert.Equal(1, (await MeasureAsync(scope, db, user, "day.comeback", Day(60))).Best);
    }

    [Fact]
    public async Task NightOwl_LocalHour()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        // 09:00Z is 05:00 in New York (EDT), outside the window.
        await AddStartedAsync(db, user, new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero));
        var now = new DateTimeOffset(2026, 6, 11, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(0, (await MeasureAsync(scope, db, user, "day.nightOwl", now, NewYork)).Best);

        // 06:00Z is 02:00 local.
        await AddStartedAsync(db, user, new DateTimeOffset(2026, 6, 10, 6, 0, 0, TimeSpan.Zero));
        Assert.Equal(1, (await MeasureAsync(scope, db, user, "day.nightOwl", now, NewYork)).Best);
    }

    [Fact]
    public async Task NewYear_LocalDate()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        var now = new DateTimeOffset(2027, 1, 2, 12, 0, 0, TimeSpan.Zero);
        // 03:00Z on Jan 1 is still Dec 31 in New York.
        await AddStartedAsync(db, user, new DateTimeOffset(2027, 1, 1, 3, 0, 0, TimeSpan.Zero));
        Assert.Equal(0, (await MeasureAsync(scope, db, user, "day.newYear", now, NewYork)).Best);

        await AddStartedAsync(db, user, new DateTimeOffset(2027, 1, 1, 17, 0, 0, TimeSpan.Zero));
        Assert.Equal(1, (await MeasureAsync(scope, db, user, "day.newYear", now, NewYork)).Best);
    }

    [Fact]
    public async Task BackdatedPrint_DoesNotCountTowardStreak()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        foreach (var d in new[] { 1, 2, 3 })
        {
            await AddStartedAsync(db, user, Day(d), loggedAfter: TimeSpan.FromDays(3));
        }

        Assert.Equal(new MetricValue(0, 0), await MeasureAsync(scope, db, user, "streak.daily", Day(4)));
        Assert.Equal(0, (await MeasureAsync(scope, db, user, "day.maxPrints", Day(4))).Best);
    }
}
