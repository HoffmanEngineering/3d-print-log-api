using Microsoft.EntityFrameworkCore;
using PrintLogApi.Achievements;
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
            new EmailAssets(wrapped),
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
        options.Campaigns[EmailOptions.CampaignKey("monthly-recap")] = new CampaignOptions { Enabled = true };

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
        var (html, text) = await MonthlyRecapTemplates.RenderAsync(renderer, GoldenModel(), CampaignTestData.Footer);

        GoldenFile.AssertMatches(html, "monthly-recap.approved.html");
        GoldenFile.AssertMatches(text, "monthly-recap.approved.txt");
    }

    // Every catalog tier earned in one month (a new user, or a backfill run), a long printer
    // name and the supporter ask: the case most likely to wrap badly or hit Gmail's clipping.
    [Fact]
    public async Task GoldenStress()
    {
        var renderer = _factory.Services.GetRequiredService<IEmailTemplateRenderer>();
        var model = StressModel(_factory.Services.GetRequiredService<EmailAssets>());

        var (html, text) = await MonthlyRecapTemplates.RenderAsync(renderer, model, AskFooter);

        Assert.Contains($"and {model.MoreBadgeCount} more", EmailLayoutRenderingTests.VisibleText(html));
        GoldenFile.AssertMatches(html, "monthly-recap-stress.approved.html");
        GoldenFile.AssertMatches(text, "monthly-recap-stress.approved.txt");
    }

    [Fact]
    public async Task GoldenSparse()
    {
        var renderer = _factory.Services.GetRequiredService<IEmailTemplateRenderer>();
        var (html, text) = await MonthlyRecapTemplates.RenderAsync(renderer, SparseModel(), CampaignTestData.Footer);

        Assert.DoesNotContain("Badges earned", html);
        Assert.DoesNotContain("▲", EmailLayoutRenderingTests.VisibleText(html));
        GoldenFile.AssertMatches(html, "monthly-recap-sparse.approved.html");
        GoldenFile.AssertMatches(text, "monthly-recap-sparse.approved.txt");
    }

    [Fact]
    public async Task Template_GainsAreGreenAndDeclinesNeutral()
    {
        var renderer = _factory.Services.GetRequiredService<IEmailTemplateRenderer>();
        var (html, _) = await MonthlyRecapTemplates.RenderAsync(renderer, GoldenModel(), CampaignTestData.Footer);

        Assert.Contains("color:#c5e1a5;\">&#x25B2; 50% vs October", html);
        Assert.Contains("color:#c5cae9;\">&#x25BC; 12% vs October", html);
    }

    [Fact]
    public async Task Render_CapsBadgesAtSixAndSkipsUnknownKeys()
    {
        var (user, _) = await UserWithPrintsAsync(null, new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            await db.UserAchievements.Where(a => a.UserId == user.Id).ExecuteDeleteAsync(Ct);
            var at = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
            foreach (var tier in Enumerable.Range(1, 6))
            {
                db.UserAchievements.Add(new UserAchievement { UserId = user.Id, AchievementKey = "prints-logged", Tier = tier, UnlockedAt = at.AddMinutes(tier) });
            }

            db.UserAchievements.Add(new UserAchievement { UserId = user.Id, AchievementKey = "no-such-badge", Tier = 1, UnlockedAt = at.AddMinutes(7) });
            db.UserAchievements.Add(new UserAchievement { UserId = user.Id, AchievementKey = "first-print", Tier = 1, UnlockedAt = at.AddMinutes(8) });
            db.UserAchievements.Add(new UserAchievement { UserId = user.Id, AchievementKey = "mcp", Tier = 1, UnlockedAt = at.AddMinutes(9) });
            await db.SaveChangesAsync(Ct);
        }

        var email = await RenderAsync(user.Id, "2026-11");

        Assert.Contains("badges/numeral-10-t1.png", email!.Html);
        Assert.Contains("badges/numeral-500-t6.png", email.Html);
        Assert.DoesNotContain("badges/robot-in.png", email.Html);
        Assert.Contains("and 2 more", EmailLayoutRenderingTests.VisibleText(email.Html));
        Assert.DoesNotContain("no-such-badge", email.Html);
    }

    [Fact]
    public async Task Render_AsksFreeUsersAndThanksProMembers()
    {
        var (free, _) = await UserWithPrintsAsync(null, new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
        var (pro, _) = await UserWithPrintsAsync(null, new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            db.Subscriptions.Add(new Subscription { UserId = pro.Id, CreatedById = pro.Id, UpdatedById = pro.Id, Status = SubscriptionStatus.Active, Plan = SubscriptionPlan.ProMonthly });
            await db.SaveChangesAsync(Ct);
        }

        var freeEmail = await RenderAsync(free.Id, "2026-11");
        var proEmail = await RenderAsync(pro.Id, "2026-11");

        Assert.Contains("going Pro", freeEmail!.Html);
        Assert.Contains("Thanks for supporting 3D Print Log", proEmail!.Html);
        Assert.DoesNotContain("going Pro", proEmail.Html);
    }

    internal static readonly EmailFooterModel AskFooter = CampaignTestData.Footer with
    {
        Supporter = SupporterLine.Ask,
        SubscriptionUrl = "https://www.3dprintlog.test/subscription",
    };

    internal static MonthlyRecapModel GoldenModel() => new(
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
        Badges:
        [
            new RecapBadge("Prolific Printer", "Silver PLA", "https://www.3dprintlog.test/assets/email/v1/badges/numeral-25-t2.png"),
            new RecapBadge("Marathon", "Bronze PLA", "https://www.3dprintlog.test/assets/email/v1/badges/timer-t1.png"),
        ],
        MoreBadgeCount: 0,
        AchievementsUrl: "https://www.3dprintlog.test/achievements",
        Tip: new RecapTip("Track your spools in Materials and see how much filament is left on each.", "https://www.3dprintlog.test/materials", "Open Materials"),
        StatsUrl: "https://www.3dprintlog.test/analytics");

    internal static MonthlyRecapModel StressModel(EmailAssets assets)
    {
        var all = AchievementCatalog.Definitions
            .SelectMany(d => Enumerable.Range(1, d.Thresholds.Count).Select(tier => (Def: d, Tier: tier)))
            .Select(x => new RecapBadge(x.Def.Title, AchievementCatalog.TierNames[x.Tier - 1], assets.Badge(BadgeImage.FileName(x.Def, x.Tier))))
            .ToList();
        return GoldenModel() with
        {
            MostUsedPrinter = "Voron 2.4 350mm Stealthburner with Klicky Probe and Nevermore (the big one)",
            Badges = all.Take(MonthlyRecapTemplates.MaxBadges).ToList(),
            MoreBadgeCount = all.Count - MonthlyRecapTemplates.MaxBadges,
        };
    }

    internal static MonthlyRecapModel SparseModel() => GoldenModel() with
    {
        SuccessRatePercent = null,
        Cost = null,
        PrintCountChangePercent = null,
        PrintHoursChangePercent = null,
        MostUsedMaterial = null,
        LongestPrint = null,
        Badges = [],
        Tip = null,
    };
}
