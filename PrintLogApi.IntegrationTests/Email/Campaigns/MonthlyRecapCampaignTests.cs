using Microsoft.EntityFrameworkCore;
using PrintLogApi.Email;
using PrintLogApi.Email.Campaigns;
using PrintLogApi.Email.Outbox;
using PrintLogApi.Email.Templates;
using PrintLogApi.Email.Tokens;
using PrintLogApi.IntegrationTests.Email.Golden;
using PrintLogApi.Models;
using PrintLogApi.Services.Analytics;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email.Campaigns;

public class MonthlyRecapCampaignTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public MonthlyRecapCampaignTests(CustomWebApplicationFactory factory) => _factory = factory;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EmailOptions Options() => new()
    {
        WebBaseUrl = "https://www.3dprintlog.test",
        ApiBaseUrl = "https://api.3dprintlog.test",
        PostalAddress = "PO Box 0, Testville, USA",
    };

    private static MonthlyRecapCampaign Campaign(IServiceScope scope)
    {
        var wrapped = Microsoft.Extensions.Options.Options.Create(Options());
        var sp = scope.ServiceProvider;
        var links = new EmailLinkBuilder(wrapped);
        return new MonthlyRecapCampaign(
            sp.GetRequiredService<PrintLogContext>(),
            sp.GetRequiredService<IAnalyticsService>(),
            sp.GetRequiredService<IEmailTemplateRenderer>(),
            new EmailFooterFactory(links, sp.GetRequiredService<IEmailTokenService>(), wrapped),
            links,
            wrapped);
    }

    private async Task<(User User, Printer Printer)> UserWithPrintsAsync(string? zone, params DateTimeOffset[] prints)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db);
        if (zone is not null)
        {
            await CampaignTestData.SetZoneAsync(db, user.Id, zone);
        }
        var printer = await CampaignTestData.PrinterAsync(db, user.Id);
        foreach (var at in prints)
        {
            await CampaignTestData.PrintAsync(db, user.Id, printer.Id, at);
        }
        return (user, printer);
    }

    private async Task<List<DueEmail>> DueForAsync(long userId, DateTimeOffset now)
    {
        using var scope = _factory.Services.CreateScope();
        return (await Campaign(scope).FindDueAsync(now, Ct)).Where(d => d.UserId == userId).ToList();
    }

    private async Task<RenderedEmail?> RenderAsync(long userId, string month)
    {
        using var scope = _factory.Services.CreateScope();
        return await Campaign(scope).RenderAsync(EmailTestData.OutboxRow(userId, "monthly-recap", month), Ct);
    }

    [Fact]
    public void Contract()
    {
        using var scope = _factory.Services.CreateScope();
        var campaign = Campaign(scope);
        Assert.Equal("monthly-recap", campaign.Name);
        Assert.Equal(EmailSettingTypes.MonthlyRecap, campaign.PreferenceSettingTypeId);
        Assert.False(campaign.ExemptFromFrequencyCap);
        Assert.True(campaign.CountsTowardFrequencyCap);
        Assert.Equal(TimeSpan.FromDays(5), campaign.Lifetime);
    }

    // Review Focus 1: the month and the 9am send follow each user's own zone.
    [Fact]
    public async Task Auckland_IsDueWhenItsDecemberStarts()
    {
        var (user, _) = await UserWithPrintsAsync("Pacific/Auckland", new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.FromHours(13)));
        var now = new DateTimeOffset(2026, 11, 30, 20, 0, 0, TimeSpan.Zero); // Dec 1, 09:00 NZDT

        Assert.Equal([new DueEmail(user.Id, "2026-11", now)], await DueForAsync(user.Id, now));
    }

    [Fact]
    public async Task Chicago_DefaultZone_IsDueAtItsOwnNineAm()
    {
        var (user, _) = await UserWithPrintsAsync(null, new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.FromHours(-6)));

        // Still November in Chicago when Auckland's recap goes out.
        Assert.Empty(await DueForAsync(user.Id, new DateTimeOffset(2026, 11, 30, 20, 0, 0, TimeSpan.Zero)));

        var nineAm = new DateTimeOffset(2026, 12, 1, 15, 0, 0, TimeSpan.Zero);
        Assert.Equal([new DueEmail(user.Id, "2026-11", nineAm)], await DueForAsync(user.Id, nineAm));
    }

    [Fact]
    public async Task PrintAtLocalMonthEdge_BelongsToItsLocalMonth()
    {
        // 23:30 on Oct 31 in Auckland is Oct 31 10:30 UTC: October, both locally and in UTC,
        // but a naive UTC month window widened for other zones must not count it for November.
        var (user, _) = await UserWithPrintsAsync("Pacific/Auckland", new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.FromHours(13)));

        Assert.Empty(await DueForAsync(user.Id, new DateTimeOffset(2026, 11, 30, 20, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task NoPrintsInTheMonth_NotDue()
    {
        var (user, _) = await UserWithPrintsAsync(null, new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));

        Assert.Empty(await DueForAsync(user.Id, new DateTimeOffset(2026, 12, 1, 15, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task EvaluatorRerun_QueuesOnce()
    {
        var (user, _) = await UserWithPrintsAsync(null, new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        var now = new DateTimeOffset(2026, 12, 1, 15, 0, 0, TimeSpan.Zero);
        var options = new EmailOptions { Enabled = true };
        options.Campaigns["monthly-recap"] = new CampaignOptions { Enabled = true };

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var evaluator = new CampaignEvaluator(db, [Campaign(scope)], Microsoft.Extensions.Options.Options.Create(options),
            new Microsoft.ApplicationInsights.TelemetryClient(new Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration()));

        await evaluator.RunOnceAsync(now, Ct);
        await evaluator.RunOnceAsync(now, Ct);

        Assert.Equal(1, db.EmailOutbox.Count(o => o.UserId == user.Id && o.Campaign == "monthly-recap"));
    }

    // ---------- rendering ----------

    [Fact]
    public async Task Render_NumbersMatchAnalytics()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var user = await EmailTestData.CreateUserAsync(db);
            var printer = await CampaignTestData.PrinterAsync(db, user.Id);
            await CampaignTestData.PrintAsync(db, user.Id, printer.Id, new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero), printSeconds: 4 * 3600);
            await CampaignTestData.PrintAsync(db, user.Id, printer.Id, new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero), printSeconds: 4 * 3600);
            await CampaignTestData.PrintAsync(db, user.Id, printer.Id, new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero), status: Print.PrintStatus.Failed, printSeconds: 4 * 3600);

            var email = await RenderAsync(user.Id, "2026-11");

            Assert.NotNull(email);
            Assert.Equal("Your November in prints: 3 prints, 12h", email.Subject);
            Assert.Contains("67% successful", email.Text);
            Assert.Contains("https://www.3dprintlog.test/analytics?utm_source=email&utm_medium=email&utm_campaign=monthly-recap&utm_content=2026-11", email.Text);
            Assert.Equal("2026-11", (string?)email.Exposure["month"]);
            Assert.Equal(3, (int?)email.Exposure["printCount"]);
            Assert.Equal(EmailSettingTypes.MonthlyRecap, email.UnsubscribeCategory);
        }
    }

    [Fact]
    public async Task Render_NoDeltaWhenPreviousMonthWasEmpty()
    {
        var (user, _) = await UserWithPrintsAsync(null, new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));

        var email = await RenderAsync(user.Id, "2026-11");

        Assert.DoesNotContain("vs October", email!.Text);
    }

    [Fact]
    public async Task Render_DeltaWhenPreviousMonthHadPrints()
    {
        var (user, _) = await UserWithPrintsAsync(null,
            new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));

        var email = await RenderAsync(user.Id, "2026-11");

        Assert.Contains("up 100% vs October", email!.Text);
    }

    [Fact]
    public async Task Render_NullWhenTheMonthHasNoPrints()
    {
        var (user, _) = await UserWithPrintsAsync(null, new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        Assert.Null(await RenderAsync(user.Id, "2026-11"));
    }

    [Fact]
    public async Task Render_BadgesEarnedThatMonthOnly()
    {
        var (user, _) = await UserWithPrintsAsync(null, new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            // Seeding the printer and print already awarded these on the real clock.
            await db.UserAchievements.Where(a => a.UserId == user.Id).ExecuteDeleteAsync(Ct);
            db.UserAchievements.Add(new UserAchievement { UserId = user.Id, AchievementKey = "first-printer", Tier = 1, UnlockedAt = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc) });
            db.UserAchievements.Add(new UserAchievement { UserId = user.Id, AchievementKey = "first-print", Tier = 1, UnlockedAt = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc) });
            await db.SaveChangesAsync(Ct);
        }

        var email = await RenderAsync(user.Id, "2026-11");

        Assert.Contains("First Layer", email!.Text);
        Assert.DoesNotContain("Bed Leveled", email.Text);
    }

    [Fact]
    public async Task Render_TipIsTheFirstUnusedFeature()
    {
        var (user, _) = await UserWithPrintsAsync(null, new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));

        // Nothing used yet: the first tip is materials.
        Assert.Contains("/materials?utm_source=email", (await RenderAsync(user.Id, "2026-11"))!.Text);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            db.Filaments.Add(new Filament
            {
                Id = Guid.NewGuid(),
                Brand = "Test",
                DisplayName = "Test PLA",
                MaterialType = "PLA",
                MaterialCategoryNickname = "filament",
                IsActive = true,
                CreatedById = user.Id,
                UpdatedById = user.Id,
            });
            await db.SaveChangesAsync(Ct);
        }

        // Materials in use: the next tip is the mobile app.
        Assert.Contains("/docs/android-app?utm_source=email", (await RenderAsync(user.Id, "2026-11"))!.Text);
    }

    [Fact]
    public async Task Render_UnparseableKey_NotRelevant()
    {
        var (user, _) = await UserWithPrintsAsync(null, new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
        Assert.Null(await RenderAsync(user.Id, "not-a-month"));
    }

    // ---------- golden ----------

    [Fact]
    public async Task Golden()
    {
        var renderer = _factory.Services.GetRequiredService<IEmailTemplateRenderer>();
        var model = new MonthlyRecapModel(
            Name: "Ada",
            MonthName: "November",
            PreviousMonthName: "October",
            PrintCount: 12,
            SuccessRatePercent: 91.7,
            PrintHours: 41.5,
            FilamentGrams: 1234,
            Cost: "$18.40",
            PrintCountChangePercent: 50,
            PrintHoursChangePercent: -12,
            MostUsedPrinter: "Voron 2.4",
            MostUsedMaterial: "Galaxy Black PETG",
            LongestPrint: "Helmet (9h)",
            Badges: ["Prolific Printer (Silver PLA)", "Marathon (Bronze PLA)"],
            Tip: new RecapTip("Track your spools in Materials and see how much filament is left on each.", "https://www.3dprintlog.test/materials", "Open Materials"),
            StatsUrl: "https://www.3dprintlog.test/analytics");

        var (html, text) = await MonthlyRecapTemplates.RenderAsync(renderer, model, CampaignTestData.Footer);

        GoldenFile.AssertMatches(html, "monthly-recap.approved.html");
        GoldenFile.AssertMatches(text, "monthly-recap.approved.txt");
    }
}
