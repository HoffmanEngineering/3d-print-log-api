using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Achievements;
using PrintLogApi.Email;
using PrintLogApi.Email.Campaigns;
using PrintLogApi.Email.Templates;
using PrintLogApi.IntegrationTests.Email.Golden;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email.Campaigns;

public class OnboardingCampaignTests : IClassFixture<CustomWebApplicationFactory>
{
    // 09:00 in Chicago (CST, UTC-6) on 2026-11-03.
    private static readonly DateTimeOffset Created = new(2026, 11, 3, 15, 0, 0, TimeSpan.Zero);

    private readonly CustomWebApplicationFactory _factory;

    public OnboardingCampaignTests(CustomWebApplicationFactory factory) => _factory = factory;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EmailOptions Options(DateTimeOffset? startDate = null) => new()
    {
        WebBaseUrl = "https://www.3dprintlog.test",
        ApiBaseUrl = "https://api.3dprintlog.test",
        PostalAddress = "PO Box 0, Testville, USA",
        Onboarding = new OnboardingOptions { StartDate = startDate ?? Created.AddDays(-30) },
    };

    private OnboardingCampaign Campaign(IServiceScope scope, EmailOptions? options = null)
    {
        var wrapped = Microsoft.Extensions.Options.Options.Create(options ?? Options());
        var sp = scope.ServiceProvider;
        return new OnboardingCampaign(
            sp.GetRequiredService<PrintLogContext>(),
            sp.GetRequiredService<IAchievementQueryService>(),
            sp.GetRequiredService<IEmailTemplateRenderer>(),
            new EmailFooterFactory(new EmailLinkBuilder(wrapped), sp.GetRequiredService<PrintLogApi.Email.Tokens.IEmailTokenService>(), wrapped),
            new EmailLinkBuilder(wrapped),
            wrapped);
    }

    private static async Task<User> UserAsync(PrintLogContext db, DateTimeOffset? created)
        => await EmailTestData.CreateUserAsync(db, createdDate: created);

    private static EmailOutbox Row(long userId, string step) => EmailTestData.OutboxRow(userId, "onboarding", step, Created);

    private async Task<IReadOnlyList<DueEmail>> DueForAsync(long userId, DateTimeOffset now, EmailOptions? options = null)
    {
        using var scope = _factory.Services.CreateScope();
        var due = await Campaign(scope, options).FindDueAsync(now, Ct);
        return due.Where(d => d.UserId == userId).ToList();
    }

    [Fact]
    public void Contract()
    {
        using var scope = _factory.Services.CreateScope();
        var campaign = Campaign(scope);
        Assert.Equal("onboarding", campaign.Name);
        Assert.Equal(EmailSettingTypes.Onboarding, campaign.PreferenceSettingTypeId);
        Assert.True(campaign.ExemptFromFrequencyCap);
        Assert.True(campaign.CountsTowardFrequencyCap);
        Assert.Equal(TimeSpan.FromDays(2), campaign.Lifetime);
    }

    [Fact]
    public async Task Schedule_FollowsSignupInUserZone()
    {
        long userId;
        using (var scope = _factory.Services.CreateScope())
        {
            userId = (await UserAsync(scope.ServiceProvider.GetRequiredService<PrintLogContext>(), Created)).Id;
        }

        // Ask at each step's own send time (Chicago stays on CST all month).
        DateTimeOffset[] askAt =
        [
            Created.AddMinutes(15),
            new(2026, 11, 5, 15, 0, 0, TimeSpan.Zero),
            new(2026, 11, 8, 15, 0, 0, TimeSpan.Zero),
            new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero),
        ];
        var due = new List<DueEmail>();
        foreach (var now in askAt)
        {
            due.AddRange(await DueForAsync(userId, now));
        }

        Assert.Equal(
            [
                new DueEmail(userId, "welcome", Created.AddMinutes(15)),
                new DueEmail(userId, "connect", new DateTimeOffset(2026, 11, 5, 15, 0, 0, TimeSpan.Zero)),
                new DueEmail(userId, "first-print", new DateTimeOffset(2026, 11, 8, 15, 0, 0, TimeSpan.Zero)),
                new DueEmail(userId, "next-steps", new DateTimeOffset(2026, 11, 13, 15, 0, 0, TimeSpan.Zero)),
            ],
            due.DistinctBy(d => d.PeriodKey).OrderBy(d => d.SendAfter));
    }

    // Steps are queued one evaluator interval ahead, never further.
    [Fact]
    public async Task Schedule_OnlyStepsWithinTheNextInterval()
    {
        long userId;
        using (var scope = _factory.Services.CreateScope())
        {
            userId = (await UserAsync(scope.ServiceProvider.GetRequiredService<PrintLogContext>(), Created)).Id;
        }

        var due = await DueForAsync(userId, Created.AddMinutes(1));

        Assert.Equal("welcome", Assert.Single(due).PeriodKey);
    }

    // A step whose whole lifetime is already behind it would only be queued to expire.
    [Fact]
    public async Task Schedule_SkipsStepsAlreadyPastTheirLifetime()
    {
        long userId;
        using (var scope = _factory.Services.CreateScope())
        {
            userId = (await UserAsync(scope.ServiceProvider.GetRequiredService<PrintLogContext>(), Created)).Id;
        }

        // Three days in: welcome (due at +15 min, lifetime 2 days) is gone; connect (day 2) is live.
        var due = await DueForAsync(userId, Created.AddDays(3));

        Assert.DoesNotContain(due, d => d.PeriodKey == "welcome");
        Assert.Contains(due, d => d.PeriodKey == "connect");
    }

    [Fact]
    public async Task NotEligible_BeforeStartDateOrWithoutSignupDate()
    {
        long early, unknown;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            early = (await UserAsync(db, Created)).Id;
            unknown = (await UserAsync(db, null)).Id;
        }

        Assert.Empty(await DueForAsync(early, Created.AddDays(1), Options(startDate: Created.AddHours(1))));
        Assert.Empty(await DueForAsync(unknown, Created.AddDays(1)));
    }

    [Fact]
    public async Task NotEligible_WithoutStartDateConfigured()
    {
        long userId;
        using (var scope = _factory.Services.CreateScope())
        {
            userId = (await UserAsync(scope.ServiceProvider.GetRequiredService<PrintLogContext>(), Created)).Id;
        }

        var options = Options();
        options.Onboarding.StartDate = null;

        Assert.Empty(await DueForAsync(userId, Created.AddDays(1), options));
    }

    [Fact]
    public async Task NotEligible_AfterFourteenDays()
    {
        long userId;
        using (var scope = _factory.Services.CreateScope())
        {
            userId = (await UserAsync(scope.ServiceProvider.GetRequiredService<PrintLogContext>(), Created)).Id;
        }

        Assert.Empty(await DueForAsync(userId, Created.AddDays(14).AddMinutes(1)));
    }

    [Fact]
    public async Task Connect_NullOnceAPrintArrivesAutomatically()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await UserAsync(db, Created);
        var printer = await CampaignTestData.PrinterAsync(db, user.Id);
        await CampaignTestData.PrintAsync(db, user.Id, printer.Id, Created.AddDays(1), PrintSource.OctoPrint);

        Assert.Null(await Campaign(scope).RenderAsync(Row(user.Id, "connect"), Ct));
    }

    [Fact]
    public async Task Connect_StillSentWhenPrintsAreOnlyManual()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await UserAsync(db, Created);
        var printer = await CampaignTestData.PrinterAsync(db, user.Id);
        await CampaignTestData.PrintAsync(db, user.Id, printer.Id, Created.AddDays(1), PrintSource.Web);

        var email = await Campaign(scope).RenderAsync(Row(user.Id, "connect"), Ct);

        Assert.NotNull(email);
        Assert.Equal("Let your prints log themselves", email.Subject);
        Assert.Contains("https://www.3dprintlog.test/docs/octoprint-webhook?utm_source=email&amp;utm_medium=email&amp;utm_campaign=onboarding&amp;utm_content=connect", email.Html);
        Assert.Contains("/docs/klipper?", email.Html);
        Assert.Contains("/docs/slic3r-uploader?", email.Html);
    }

    [Fact]
    public async Task FirstPrint_NullWhenAnyPrintExists()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await UserAsync(db, Created);
        var printer = await CampaignTestData.PrinterAsync(db, user.Id);
        await CampaignTestData.PrintAsync(db, user.Id, printer.Id, Created.AddDays(1));

        Assert.Null(await Campaign(scope).RenderAsync(Row(user.Id, "first-print"), Ct));
    }

    [Fact]
    public async Task FirstPrint_SentWithNoPrints()
    {
        using var scope = _factory.Services.CreateScope();
        var user = await UserAsync(scope.ServiceProvider.GetRequiredService<PrintLogContext>(), Created);

        var email = await Campaign(scope).RenderAsync(Row(user.Id, "first-print"), Ct);

        Assert.NotNull(email);
        Assert.Equal("Log your first print in 30 seconds", email.Subject);
        Assert.Contains("/prints/new/edit?utm_source=email", email.Html);
    }

    [Fact]
    public async Task NextSteps_NullWithZeroPrints()
    {
        using var scope = _factory.Services.CreateScope();
        var user = await UserAsync(scope.ServiceProvider.GetRequiredService<PrintLogContext>(), Created);

        Assert.Null(await Campaign(scope).RenderAsync(Row(user.Id, "next-steps"), Ct));
    }

    [Fact]
    public async Task NextSteps_ShowsStats()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await UserAsync(db, Created);
        var printer = await CampaignTestData.PrinterAsync(db, user.Id);
        await CampaignTestData.PrintAsync(db, user.Id, printer.Id, Created.AddDays(1), printSeconds: 2 * 3600);
        await CampaignTestData.PrintAsync(db, user.Id, printer.Id, Created.AddDays(2), printSeconds: 3600);

        var email = await Campaign(scope).RenderAsync(Row(user.Id, "next-steps"), Ct);

        Assert.NotNull(email);
        Assert.Equal("Your first prints, and what's next", email.Subject);
        Assert.Contains("2 prints", email.Text);
        Assert.Contains("3h of print time", email.Text);
    }

    [Fact]
    public async Task Welcome_CtaIsTheNextHintWithUtm()
    {
        using var scope = _factory.Services.CreateScope();
        var user = await UserAsync(scope.ServiceProvider.GetRequiredService<PrintLogContext>(), Created);

        var email = await Campaign(scope).RenderAsync(Row(user.Id, "welcome"), Ct);

        // A brand-new user's first hint is "add your first printer".
        Assert.NotNull(email);
        Assert.Equal("Welcome to 3D Print Log", email.Subject);
        Assert.Contains("href=\"https://www.3dprintlog.test/printers/new?utm_source=email&amp;utm_medium=email&amp;utm_campaign=onboarding&amp;utm_content=welcome\"", email.Html);
        Assert.Equal(EmailSettingTypes.Onboarding, email.UnsubscribeCategory);
        Assert.Equal("welcome", (string?)email.Exposure["step"]);
    }

    [Fact]
    public async Task UnknownStep_IsNotRelevant()
    {
        using var scope = _factory.Services.CreateScope();
        var user = await UserAsync(scope.ServiceProvider.GetRequiredService<PrintLogContext>(), Created);

        Assert.Null(await Campaign(scope).RenderAsync(Row(user.Id, "retired-step"), Ct));
    }

    // ---------- golden renderings (fixed models, so tokens and ids do not vary) ----------

    public static TheoryData<string> Steps => new() { "welcome", "connect", "first-print", "next-steps" };

    [Theory]
    [MemberData(nameof(Steps))]
    public async Task Golden(string step)
    {
        var renderer = _factory.Services.GetRequiredService<IEmailTemplateRenderer>();
        var (html, text) = await OnboardingTemplates.RenderAsync(renderer, GoldenModel(step), CampaignTestData.Footer);

        GoldenFile.AssertMatches(html, $"onboarding-{step}.approved.html");
        GoldenFile.AssertMatches(text, $"onboarding-{step}.approved.txt");
    }

    [Fact]
    public async Task NextSteps_ShowsABadgeMeterAndTheNextBadge()
    {
        var renderer = _factory.Services.GetRequiredService<IEmailTemplateRenderer>();
        var (html, _) = await OnboardingTemplates.RenderAsync(renderer, GoldenModel(OnboardingTemplates.NextSteps), CampaignTestData.Footer);

        Assert.Equal(2, Regex.Count(html, "class=\"em-meter-on\""));
        Assert.Equal(4, Regex.Count(html, "class=\"em-meter-off\""));
        Assert.Contains("Next badge", html);
        Assert.Contains("Add your first printer.", html);
    }

    [Fact]
    public async Task Connect_ShowsEachIntegrationAsACard()
    {
        var renderer = _factory.Services.GetRequiredService<IEmailTemplateRenderer>();
        var (html, _) = await OnboardingTemplates.RenderAsync(renderer, GoldenModel(OnboardingTemplates.Connect), CampaignTestData.Footer);

        Assert.Equal(3, Regex.Count(html, "class=\"em-panel\""));
    }

    internal static OnboardingModel GoldenModel(string step) => new(
        Step: step,
        Name: "Ada",
        CtaUrl: "https://www.3dprintlog.test/printers/new?utm_source=email",
        CtaLabel: "Add your printer",
        HintText: "Add your first printer.",
        PrintCount: 4,
        PrintHours: 12,
        FilamentGrams: 1234,
        GettingStartedHeld: 2,
        GettingStartedTotal: 6,
        OctoPrintUrl: "https://www.3dprintlog.test/docs/octoprint-webhook",
        KlipperUrl: "https://www.3dprintlog.test/docs/klipper",
        SlicerUrl: "https://www.3dprintlog.test/docs/slic3r-uploader",
        FirstPrintUrl: "https://www.3dprintlog.test/prints/new/edit");
}
