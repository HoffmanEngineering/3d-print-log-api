using PrintLogApi.Email;
using PrintLogApi.Email.Campaigns;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email.Campaigns;

/// <summary>
/// The monthly recap only queries Prints while some zone can be due (<see cref="MonthlyRecapCampaign.SendWindow"/>).
/// These pin the window's two ends to the instants the due test itself allows, for the extreme
/// zones, and check that a tick one interval outside either end sends no query at all.
/// </summary>
public class MonthlyRecapSendWindowTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(15);

    // Defaults: 09:00 local, every 15 minutes, a 5-day lifetime.
    private const int Hour = 9;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The window opens when the first zone (+14:00) is one tick before its 09:00 on the 1st,
    // and closes when the last zone's (-12:00) row would expire, five days after its 09:00.
    [Theory]
    [InlineData(2026, 11, "2026-11-30T18:45:00Z", "2026-12-06T21:00:00Z")]
    [InlineData(2026, 12, "2026-12-31T18:45:00Z", "2027-01-06T21:00:00Z")]
    [InlineData(2027, 1, "2027-01-31T18:45:00Z", "2027-02-06T21:00:00Z")]
    [InlineData(2028, 2, "2028-02-29T18:45:00Z", "2028-03-06T21:00:00Z")]
    public void SendWindow_DefaultSettings(int year, int month, string opens, string closes)
    {
        var window = MonthlyRecapCampaign.SendWindow(new DateOnly(year, month, 1), Hour, Tick);

        Assert.Equal(DateTimeOffset.Parse(opens, System.Globalization.CultureInfo.InvariantCulture), window.Opens);
        Assert.Equal(DateTimeOffset.Parse(closes, System.Globalization.CultureInfo.InvariantCulture), window.Closes);
    }

    // The window's derivation rests on every zone's 09:00 (or any hour) on the 1st falling
    // within [M + hour - 14h, M + hour + 12h], gap shifts included. Checked for every zone this
    // host knows, every send hour and every month for six years; and both ends are reached, so
    // the window is no wider than the zones make it.
    [Fact]
    public void EveryZonesSendAfter_LiesInTheWindow_AndTheExtremeZonesReachItsEnds()
    {
        var zones = TimeZoneInfo.GetSystemTimeZones();
        for (var hour = 0; hour < 24; hour++)
        {
            for (var first = new DateOnly(2026, 1, 1); first < new DateOnly(2032, 1, 1); first = first.AddMonths(1))
            {
                var wallClock = new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddHours(hour);
                var earliest = DateTimeOffset.MaxValue;
                var latest = DateTimeOffset.MinValue;
                foreach (var zone in zones)
                {
                    var sendAfter = EmailTimeZones.AtLocal(first, hour, zone);
                    Assert.InRange(sendAfter, wallClock - TimeSpan.FromHours(14), wallClock + TimeSpan.FromHours(12));
                    earliest = sendAfter < earliest ? sendAfter : earliest;
                    latest = sendAfter > latest ? sendAfter : latest;
                }

                Assert.Equal(wallClock - TimeSpan.FromHours(14), earliest);
                Assert.Equal(wallClock + TimeSpan.FromHours(12), latest);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(23)]
    public void CanBeDue_IsTheHalfOpenWindow(int hour)
    {
        var month = new DateOnly(2026, 12, 1);
        var (opens, closes) = MonthlyRecapCampaign.SendWindow(month, hour, Tick);

        Assert.False(MonthlyRecapCampaign.CanBeDue(month, opens.AddTicks(-1), hour, Tick));
        Assert.True(MonthlyRecapCampaign.CanBeDue(month, opens, hour, Tick));
        Assert.True(MonthlyRecapCampaign.CanBeDue(month, closes.AddTicks(-1), hour, Tick));
        Assert.False(MonthlyRecapCampaign.CanBeDue(month, closes, hour, Tick));
    }

    // First eligible instant, across a year boundary: Kiritimati (+14:00) reaches 09:00 on
    // Jan 1 at 19:00 UTC on Dec 31, so the tick at 18:45 queues it and the tick before it
    // must not even look.
    [Fact]
    public async Task FirstEligibleInstant_KiritimatiOnDecember31()
    {
        var user = await UserWithPrintAsync("Pacific/Kiritimati", new DateTimeOffset(2026, 12, 15, 12, 0, 0, TimeSpan.Zero));
        var opens = new DateTimeOffset(2026, 12, 31, 18, 45, 0, TimeSpan.Zero);

        var (due, printsQueries) = await DueAsync(user.Id, opens);
        Assert.Equal([new DueEmail(user.Id, "2026-12", new DateTimeOffset(2026, 12, 31, 19, 0, 0, TimeSpan.Zero))], due);
        Assert.Single(printsQueries);
        Assert.Equal(due, await LegacyDueAsync(user.Id, opens));

        var (before, beforeQueries) = await DueAsync(user.Id, opens - Tick);
        Assert.Empty(before);
        Assert.Empty(beforeQueries);
        Assert.Empty(await LegacyDueAsync(user.Id, opens - Tick));
    }

    // Last eligible instant, across a month boundary: Etc/GMT+12 reaches 09:00 on Dec 1 at
    // 21:00 UTC, and its row expires five days later. The last tick before that still queues it;
    // a tick at the expiry instant sends no query.
    [Fact]
    public async Task LastEligibleInstant_GmtMinus12FiveDaysIn()
    {
        var user = await UserWithPrintAsync("Etc/GMT+12", new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        var closes = new DateTimeOffset(2026, 12, 6, 21, 0, 0, TimeSpan.Zero);

        var (due, printsQueries) = await DueAsync(user.Id, closes - Tick);
        Assert.Equal([new DueEmail(user.Id, "2026-11", new DateTimeOffset(2026, 12, 1, 21, 0, 0, TimeSpan.Zero))], due);
        Assert.Single(printsQueries);
        Assert.Equal(due, await LegacyDueAsync(user.Id, closes - Tick));

        var (after, afterQueries) = await DueAsync(user.Id, closes);
        Assert.Empty(after);
        Assert.Empty(afterQueries);
        Assert.Empty(await LegacyDueAsync(user.Id, closes));
    }

    // Mid-month, the old code ran its two month-wide queries on every tick for nothing.
    [Fact]
    public async Task MidMonthTick_SendsNoQueryAtAll()
    {
        var user = await UserWithPrintAsync(null, new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        using var scope = factory.Services.CreateScope();
        var (db, recorder) = CampaignQueryTestKit.RecordingContext(scope.ServiceProvider);

        Assert.Empty(await CampaignQueryTestKit.MonthlyRecap(scope.ServiceProvider, db).FindDueAsync(new DateTimeOffset(2026, 12, 15, 12, 0, 0, TimeSpan.Zero), Ct));

        Assert.Empty(recorder.Commands);
        Assert.Empty(await LegacyDueAsync(user.Id, new DateTimeOffset(2026, 12, 15, 12, 0, 0, TimeSpan.Zero)));
    }

    private async Task<User> UserWithPrintAsync(string? zone, DateTimeOffset at)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db);
        if (zone is not null)
        {
            await CampaignTestData.SetZoneAsync(db, user.Id, zone);
        }

        var printer = await CampaignTestData.PrinterAsync(db, user.Id);
        await CampaignTestData.PrintAsync(db, user.Id, printer.Id, at);
        return user;
    }

    private async Task<(List<DueEmail> Due, List<string> PrintsQueries)> DueAsync(long userId, DateTimeOffset now)
    {
        using var scope = factory.Services.CreateScope();
        var (db, recorder) = CampaignQueryTestKit.RecordingContext(scope.ServiceProvider);
        var due = await CampaignQueryTestKit.MonthlyRecap(scope.ServiceProvider, db).FindDueAsync(now, Ct);
        return (due.Where(d => d.UserId == userId).ToList(), recorder.PrintsQueries);
    }

    private async Task<List<DueEmail>> LegacyDueAsync(long userId, DateTimeOffset now)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return (await LegacyCampaignQueries.MonthlyRecapDueAsync(db, CampaignQueryTestKit.Options(), now, Ct))
            .Where(d => d.UserId == userId)
            .ToList();
    }
}
