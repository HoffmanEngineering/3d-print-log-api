using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Email;
using PrintLogApi.Email.Campaigns;
using PrintLogApi.Email.Outbox;
using PrintLogApi.Models;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace PrintLogApi.IntegrationTests.Email.Campaigns;

/// <summary>
/// The monthly recap and printer-silent due queries were narrowed so the 15-minute evaluator
/// stops reading Prints when it cannot produce a send. These hold the narrowed queries to the
/// old ones (<see cref="LegacyCampaignQueries"/>) user for user, tick for tick, on a seeded set
/// built around every edge the two campaigns have, and pin which ticks touch Prints.
/// </summary>
public class CampaignQueryNarrowingTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------- monthly recap ----------

    private static readonly string?[] RecapZones =
    [
        null, "Pacific/Kiritimati", "Etc/GMT+12", "Pacific/Auckland", "Asia/Kolkata",
        "Pacific/Chatham", "America/St_Johns", "Australia/Lord_Howe", "Europe/London",
    ];

    /// <summary>
    /// For each zone, users whose only November print sits on or just beside a local month edge,
    /// one well inside it, one with October only, and one already queued; half also print in
    /// December, for the January recap.
    /// </summary>
    private async Task<HashSet<long>> SeedRecapAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var settings = CampaignQueryTestKit.Options();
        var ids = new HashSet<long>();
        var november = new DateOnly(2026, 11, 1);

        foreach (var zoneId in RecapZones)
        {
            var zone = EmailTimeZones.For(zoneId, settings.DefaultTimeZone);
            var localStart = EmailTimeZones.AtLocal(november, 0, zone);
            var localEnd = EmailTimeZones.AtLocal(november.AddMonths(1), 0, zone);
            DateTimeOffset[] prints =
            [
                localStart,
                localStart.AddTicks(-1),
                localEnd,
                localEnd.AddTicks(-1),
                new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero),
            ];

            for (var i = 0; i < prints.Length; i++)
            {
                var user = await EmailTestData.CreateUserAsync(db);
                ids.Add(user.Id);
                if (zoneId is not null)
                {
                    await CampaignTestData.SetZoneAsync(db, user.Id, zoneId);
                }

                var printer = await CampaignTestData.PrinterAsync(db, user.Id);
                await CampaignTestData.PrintAsync(db, user.Id, printer.Id, prints[i]);
                if (i % 2 == 0)
                {
                    await CampaignTestData.PrintAsync(db, user.Id, printer.Id, new DateTimeOffset(2026, 12, 15, 12, 0, 0, TimeSpan.Zero));
                }
            }
        }

        return ids;
    }

    /// <summary>Every 15 minutes around both ends of the November and December windows, every 3 hours (off the quarter hour) elsewhere.</summary>
    private static IEnumerable<DateTimeOffset> RecapTicks()
    {
        var dense = new[]
        {
            (new DateTimeOffset(2026, 11, 30, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 2, 12, 0, 0, TimeSpan.Zero)),
            (new DateTimeOffset(2026, 12, 6, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 7, 3, 0, 0, TimeSpan.Zero)),
            (new DateTimeOffset(2026, 12, 31, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 1, 1, 12, 0, 0, TimeSpan.Zero)),
            (new DateTimeOffset(2027, 1, 6, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 1, 7, 3, 0, 0, TimeSpan.Zero)),
        };
        foreach (var (from, to) in dense)
        {
            for (var t = from; t < to; t += TimeSpan.FromMinutes(15))
            {
                yield return t;
            }
        }

        for (var t = new DateTimeOffset(2026, 11, 25, 0, 7, 0, TimeSpan.Zero); t < new DateTimeOffset(2027, 1, 10, 0, 0, 0, TimeSpan.Zero); t += TimeSpan.FromHours(3))
        {
            yield return t;
        }
    }

    [Fact]
    public async Task MonthlyRecap_SameDueAsBefore_AtEveryTick()
    {
        var ids = await SeedRecapAsync();
        var settings = CampaignQueryTestKit.Options();
        var dueTicks = 0;

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var campaign = CampaignQueryTestKit.MonthlyRecap(scope.ServiceProvider, db, settings);
        foreach (var now in RecapTicks())
        {
            var expected = (await LegacyCampaignQueries.MonthlyRecapDueAsync(db, settings, now, Ct)).Where(d => ids.Contains(d.UserId)).OrderBy(d => d.UserId).ThenBy(d => d.PeriodKey).ToList();
            var actual = (await campaign.FindDueAsync(now, Ct)).Where(d => ids.Contains(d.UserId)).OrderBy(d => d.UserId).ThenBy(d => d.PeriodKey).ToList();

            Assert.True(expected.SequenceEqual(actual), $"Differs at {now:O}: expected {string.Join(", ", expected)}; got {string.Join(", ", actual)}");
            dueTicks += expected.Count > 0 ? 1 : 0;
        }

        // Not vacuous: both months queue across many ticks, from every zone.
        Assert.True(dueTicks > 100, $"Only {dueTicks} ticks had anyone due.");
    }

    [Fact]
    public async Task MonthlyRecap_LeavesOutUsersAlreadyQueued_AsTheEvaluatorWould()
    {
        var now = new DateTimeOffset(2026, 12, 1, 15, 0, 0, TimeSpan.Zero);
        long queued, fresh;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            queued = (await EmailTestData.CreateUserAsync(db)).Id;
            fresh = (await EmailTestData.CreateUserAsync(db)).Id;
            foreach (var userId in new[] { queued, fresh })
            {
                var printer = await CampaignTestData.PrinterAsync(db, userId);
                await CampaignTestData.PrintAsync(db, userId, printer.Id, new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
            }

            // Queued for November; an October row is a different period and does not count.
            db.EmailOutbox.Add(EmailTestData.OutboxRow(queued, MonthlyRecapCampaign.CampaignName, "2026-11", now.AddHours(-1)));
            db.EmailOutbox.Add(EmailTestData.OutboxRow(fresh, MonthlyRecapCampaign.CampaignName, "2026-10", now.AddDays(-30)));
            await db.SaveChangesAsync(Ct);
        }

        using var runScope = factory.Services.CreateScope();
        var runDb = runScope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var due = await CampaignQueryTestKit.MonthlyRecap(runScope.ServiceProvider, runDb).FindDueAsync(now, Ct);

        Assert.DoesNotContain(due, d => d.UserId == queued);
        Assert.Contains(due, d => d.UserId == fresh && d.PeriodKey == "2026-11");
    }

    // The evaluator runs in dry run exactly as it does when sending, so dry run gets the same
    // savings: a mid-month tick reads nothing from Prints for the recap.
    [Fact]
    public async Task DryRunEvaluator_MidMonthTick_ReadsNoPrintsForTheRecap()
    {
        var settings = CampaignQueryTestKit.Options();
        settings.DryRun = true;
        settings.Campaigns[EmailOptions.CampaignKey(MonthlyRecapCampaign.CampaignName)] = new CampaignOptions { Enabled = true };

        using var scope = factory.Services.CreateScope();
        var (db, recorder) = CampaignQueryTestKit.RecordingContext(scope.ServiceProvider);
        var evaluator = new CampaignEvaluator(
            db,
            [CampaignQueryTestKit.MonthlyRecap(scope.ServiceProvider, db, settings)],
            MsOptions.Create(settings),
            new TelemetryClient(new TelemetryConfiguration()));

        Assert.Equal(0, await evaluator.RunOnceAsync(new DateTimeOffset(2026, 12, 15, 12, 0, 0, TimeSpan.Zero), Ct));
        Assert.Empty(recorder.PrintsQueries);

        // Inside the window the same dry-run tick does read, once.
        await evaluator.RunOnceAsync(new DateTimeOffset(2026, 12, 1, 15, 0, 0, TimeSpan.Zero), Ct);
        Assert.Single(recorder.PrintsQueries);
    }

    // ---------- printer silent ----------

    private static readonly DateTimeOffset SilentStart = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Printers whose last automated print lands on, just inside and just outside both ends of
    /// the 14-21 day window during the sweep, with two or three prints of history, history older
    /// than 90 days before the last, inactive printers, manual prints after the last, two
    /// printers on one user, and a user alerted within the repeat guard.
    /// </summary>
    private async Task<HashSet<long>> SeedSilentAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var ids = new HashSet<long>();

        async Task<(long User, Printer Printer)> PrinterWith(string? zone, bool active, PrintSource source, params DateTimeOffset[] prints)
        {
            var user = await EmailTestData.CreateUserAsync(db);
            ids.Add(user.Id);
            if (zone is not null)
            {
                await CampaignTestData.SetZoneAsync(db, user.Id, zone);
            }

            var printer = await CampaignTestData.PrinterAsync(db, user.Id, active: active);
            foreach (var at in prints)
            {
                await CampaignTestData.PrintAsync(db, user.Id, printer.Id, at, source);
            }

            return (user.Id, printer);
        }

        DateTimeOffset Day(double days) => SilentStart.AddDays(days);

        // Last prints spread across the sweep, some on exact hours, some between them.
        foreach (var last in new[] { -20.0, -14.0, -7.0, -6.99, 0.0, 0.5, 3.25, 9.0 })
        {
            await PrinterWith(null, true, PrintSource.Moonraker, Day(last), Day(last - 10), Day(last - 20));
            await PrinterWith("Europe/Berlin", true, PrintSource.OctoPrint, Day(last), Day(last - 30));
            await PrinterWith("Pacific/Kiritimati", true, PrintSource.Bambu, Day(last), Day(last - 89), Day(last - 90));
            await PrinterWith("Etc/GMT+12", true, PrintSource.ApiKey, Day(last), Day(last - 89), Day(last - 91));
            await PrinterWith(null, false, PrintSource.SlicerPlugin, Day(last), Day(last - 10), Day(last - 20));
        }

        // A manual print after the last automated one does not end the silence.
        var (manual, manualPrinter) = await PrinterWith(null, true, PrintSource.PrusaLink, Day(0), Day(-5), Day(-10));
        await CampaignTestData.PrintAsync(db, manual, manualPrinter.Id, Day(16), PrintSource.Web);

        // Two printers on one user: the email keys on the later silence.
        var (two, _) = await PrinterWith(null, true, PrintSource.Sdcp, Day(1), Day(-9), Day(-19));
        var second = await CampaignTestData.PrinterAsync(db, two, "Second");
        foreach (var at in new[] { Day(4), Day(-6), Day(-16) })
        {
            await CampaignTestData.PrintAsync(db, two, second.Id, at, PrintSource.Duet);
        }

        // Alerted within the last 30 days: never due during the sweep.
        var (alerted, _) = await PrinterWith(null, true, PrintSource.Anycubic, Day(0), Day(-10), Day(-20));
        db.EmailOutbox.Add(EmailTestData.OutboxRow(alerted, PrinterSilentCampaign.CampaignName, "silent:2026-10-01", Day(5)));
        await db.SaveChangesAsync(Ct);

        return ids;
    }

    [Fact]
    public async Task PrinterSilent_SameDueAsBefore_AtEveryTick()
    {
        var ids = await SeedSilentAsync();
        var settings = CampaignQueryTestKit.Options();
        var dueTicks = 0;

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var campaign = CampaignQueryTestKit.PrinterSilent(scope.ServiceProvider, db, settings);

        // Hourly, plus the quarter hours, from before any printer is quiet to after all are.
        for (var now = SilentStart.AddDays(5); now < SilentStart.AddDays(32); now += TimeSpan.FromMinutes(75))
        {
            var expected = (await LegacyCampaignQueries.PrinterSilentDueAsync(db, settings, now, Ct)).Where(d => ids.Contains(d.UserId)).OrderBy(d => d.UserId).ToList();
            var actual = (await campaign.FindDueAsync(now, Ct)).Where(d => ids.Contains(d.UserId)).OrderBy(d => d.UserId).ToList();

            Assert.True(expected.SequenceEqual(actual), $"Differs at {now:O}: expected {string.Join(", ", expected)}; got {string.Join(", ", actual)}");
            dueTicks += expected.Count > 0 ? 1 : 0;
        }

        Assert.True(dueTicks > 100, $"Only {dueTicks} ticks had anyone due.");
    }

    // A tick with no printer in the window aggregates in SQL and loads no print rows; the old
    // code loaded every automated print of the last 111 days on every tick.
    [Fact]
    public async Task PrinterSilent_NoPrinterInTheWindow_OnlyTheAggregateQuery()
    {
        var now = new DateTimeOffset(2031, 6, 1, 15, 0, 0, TimeSpan.Zero);
        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<PrintLogContext>();
            var user = await EmailTestData.CreateUserAsync(db);
            var printer = await CampaignTestData.PrinterAsync(db, user.Id);
            foreach (var daysAgo in new[] { 2, 12, 22 })
            {
                await CampaignTestData.PrintAsync(db, user.Id, printer.Id, now.AddDays(-daysAgo), PrintSource.Moonraker);
            }
        }

        using var scope = factory.Services.CreateScope();
        var (recording, recorder) = CampaignQueryTestKit.RecordingContext(scope.ServiceProvider);

        Assert.Empty(await CampaignQueryTestKit.PrinterSilent(scope.ServiceProvider, recording).FindDueAsync(now, Ct));

        var query = Assert.Single(recorder.Commands);
        Assert.Contains("GROUP BY", query);
        Assert.Contains("HAVING", query);
    }

    [Fact]
    public void PrinterSilent_CandidateQueryHasTheShapeSqlServerAccepts()
    {
        using var context = new PrintLogContext(new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlServer("Server=unused;Database=unused;Trusted_Connection=True;")
            .Options);
        var now = new DateTime(2026, 11, 20, 15, 0, 0, DateTimeKind.Utc);

        var sql = PrinterSilentCampaign.PrintersLastHeardBetween(context, now.AddDays(-21), now.AddDays(-14)).ToQueryString();

        // One aggregate in the HAVING, none nested inside another (error 8124).
        Assert.Contains("GROUP BY [p].[PrinterId]", sql);
        Assert.Contains("HAVING MAX([p].[CreatedDate]) <=", sql);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(sql, "MAX\\("));
    }
}
