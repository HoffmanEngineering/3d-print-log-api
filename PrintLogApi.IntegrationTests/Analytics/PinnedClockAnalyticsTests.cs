using PrintLogApi.Models.DTOs.Analytics;
using PrintLogApi.Services.Analytics;
using Xunit;

namespace PrintLogApi.IntegrationTests.Analytics;

/// <summary>
/// The boundaries that only a controllable clock can reach: the day a streak lapses, and the
/// day a print ages out of the trailing burn-rate window a runway is computed from.
///
/// Both were previously untestable — not awkward, untestable. The date is an input to the
/// answer and the only way to supply it was to be running on the right day.
///
/// These tests move the CLOCK and leave the fixture alone, which is the point: the rows are
/// identical at every instant probed below and only "now" differs, so a failure can only be a
/// date-math change. Constructing an equivalent fixture relative to the real today would prove
/// the same arithmetic against data that also moved.
/// </summary>
/// <remarks>
/// The fixture's clock is mutable shared state, so these tests must not interleave. They
/// cannot: xunit parallelizes across test COLLECTIONS, and a class with no explicit collection
/// is its own — the facts below run one after another. Keep it that way; adding a
/// [Collection] shared with another class would break the isolation silently.
/// </remarks>
public class PinnedClockAnalyticsTests : IClassFixture<PinnedClockDataFactory>
{
    private readonly PinnedClockDataFactory _factory;

    public PinnedClockAnalyticsTests(PinnedClockDataFactory factory) => _factory = factory;

    /// <summary>
    /// An all-time filter, which is what makes the clock load-bearing: with no ToDate the
    /// window's open end IS "now", so the answer is a function of the clock and nothing else.
    /// </summary>
    private static AnalyticsFilter AllTime() => new() { TimeZone = "UTC" };

    private async Task<ActivityResponse> Activity(DateTimeOffset now)
    {
        _factory.Clock.SetUtcNow(now);
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IActivityAnalyticsService>();
        var filter = AllTime();
        filter.Normalize(now);
        return await service.GetActivity(_factory.UserId, filter, CancellationToken.None);
    }

    private async Task<MaterialsResponse> Materials(DateTimeOffset now)
    {
        _factory.Clock.SetUtcNow(now);
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IMaterialAnalyticsService>();
        var filter = AllTime();
        filter.Normalize(now);
        return await service.GetMaterials(_factory.UserId, filter, CancellationToken.None);
    }

    [Fact]
    public async Task Streak_IsCurrentOnTheLastPrintDay_SurvivesTheGraceDay_AndLapsesTheDayAfter()
    {
        var pinned = PinnedClockDataFactory.Pinned;

        var onTheDay = await Activity(pinned);
        var graceDay = await Activity(pinned.AddDays(1));
        var lapsed = await Activity(pinned.AddDays(2));

        // Three consecutive days of prints, ending on the pinned today.
        Assert.Equal(3, onTheDay.Streaks.CurrentDays);

        // Still current a day later. This is the branch a real-clock test cannot pin: it
        // exists because a user who has not printed YET today is not someone whose streak has
        // ended, and it is one day wide.
        Assert.Equal(3, graceDay.Streaks.CurrentDays);

        // And gone the day after. Zero, not 2 — a lapsed streak is not a shorter streak.
        Assert.Equal(0, lapsed.Streaks.CurrentDays);

        // The LONGEST run is a property of the data, so it is the same number at all three
        // instants. Asserted alongside CurrentDays because a bug that conflated the two would
        // otherwise pass the first assertion above.
        Assert.Equal(3, onTheDay.Streaks.LongestDays);
        Assert.Equal(3, graceDay.Streaks.LongestDays);
        Assert.Equal(3, lapsed.Streaks.LongestDays);
    }

    [Fact]
    public async Task Runway_LengthensWhenAPrintAgesOutOfTheTrailingBurnWindow()
    {
        var pinned = PinnedClockDataFactory.Pinned;

        var before = Assert.Single((await Materials(pinned)).Runway);
        var after = Assert.Single((await Materials(pinned.AddDays(2))).Runway);

        // The spool is untouched by the clock: 200 g initial, 150 g consumed across the four
        // prints, whenever "now" is.
        const double remainingGrams = 50.0;
        Assert.Equal(remainingGrams, before.RemainingGrams, 6);
        Assert.Equal(remainingGrams, after.RemainingGrams, 6);

        // At Pinned the day -89 print is one day inside the 90-day burn window, so all
        // 150 g count: 150 / 90 days.
        Assert.Equal(150.0 / 90.0, before.BurnRateGramsPerDay, 6);
        Assert.Equal(remainingGrams / (150.0 / 90.0), before.RunwayDays!.Value, 6);

        // Two days later that print is at day -91 and drops out. The window is still 90 days
        // wide — only its contents changed — so the burn rate falls to 60 / 90 and the same
        // 50 g of filament is now predicted to last two and a half times as long.
        Assert.Equal(60.0 / 90.0, after.BurnRateGramsPerDay, 6);
        Assert.Equal(remainingGrams / (60.0 / 90.0), after.RunwayDays!.Value, 6);

        // Stated as exact numbers so the intent survives a refactor of the arithmetic above:
        // 30 days becomes 75.
        Assert.Equal(30.0, before.RunwayDays!.Value, 6);
        Assert.Equal(75.0, after.RunwayDays!.Value, 6);
    }

    /// <summary>
    /// The two tests above call the service directly and normalize the filter themselves, so
    /// neither would notice the CONTROLLER going back to ambient time. This one goes over
    /// HTTP and pins the one thing the controller's own clock read decides: the ceiling
    /// <c>Normalize</c> clamps a future <c>toDate</c> to, which is also what lands in the
    /// cache key.
    ///
    /// It is decisive rather than merely consistent. The requested <c>toDate</c> is AFTER the
    /// pinned clock but BEFORE the real one, so an ambient-clock controller would not clamp it
    /// at all and would echo the requested date straight back.
    /// </summary>
    [Fact]
    public async Task Controller_ClampsAFutureToDateAgainstTheInjectedClock_NotTheWallClock()
    {
        var pinned = PinnedClockDataFactory.Pinned;
        _factory.Clock.SetUtcNow(pinned);

        var from = pinned.AddDays(-30);
        var requestedTo = pinned.AddDays(10);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.TestUserIdHeader, PinnedClockDataFactory.PinnedUserOAuthId);

        var response = await client.GetAsync(
            "/api/Analytics/activity"
            + $"?fromDate={Uri.EscapeDataString(from.ToString("O"))}"
            + $"&toDate={Uri.EscapeDataString(requestedTo.ToString("O"))}"
            + "&timeZone=UTC", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ActivityResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);

        // ClampCeiling rounds UP to the next whole UTC hour, so a midday pinned clock gives
        // 13:00. Asserted through the public helper rather than as a literal so the two cannot
        // drift apart, and against a literal too so a change to the helper is not silently
        // absorbed by both sides of the assertion.
        Assert.Equal(AnalyticsFilter.ClampCeiling(pinned), body.To);
        Assert.Equal(new DateTimeOffset(2025, 6, 18, 13, 0, 0, TimeSpan.Zero), body.To);

        // And it is genuinely a clamp, not a passthrough that happens to match.
        Assert.NotEqual(requestedTo, body.To);
    }
}
