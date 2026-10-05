using Microsoft.EntityFrameworkCore;
using PrintLogApi.Email;
using PrintLogApi.Email.Campaigns;
using PrintLogApi.Email.Templates;
using PrintLogApi.Email.Tokens;
using PrintLogApi.IntegrationTests.Email.Golden;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email.Campaigns;

public class PrinterSilentCampaignTests : IClassFixture<CustomWebApplicationFactory>
{
    // 15:00Z = 09:00 Chicago (CST).
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);

    private readonly CustomWebApplicationFactory _factory;

    public PrinterSilentCampaignTests(CustomWebApplicationFactory factory) => _factory = factory;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EmailOptions Options() => new()
    {
        WebBaseUrl = "https://www.3dprintlog.test",
        ApiBaseUrl = "https://api.3dprintlog.test",
        PostalAddress = "PO Box 0, Testville, USA",
    };

    private static PrinterSilentCampaign Campaign(IServiceScope scope)
    {
        var wrapped = Microsoft.Extensions.Options.Options.Create(Options());
        var sp = scope.ServiceProvider;
        var links = new EmailLinkBuilder(wrapped);
        return new PrinterSilentCampaign(
            sp.GetRequiredService<PrintLogContext>(),
            sp.GetRequiredService<IEmailTemplateRenderer>(),
            new EmailFooterFactory(links, sp.GetRequiredService<IEmailTokenService>(), wrapped),
            links,
            wrapped);
    }

    /// <summary>A user with one active printer whose last automated print was <paramref name="ago"/> before <see cref="Now"/>, preceded by <paramref name="earlier"/> more within 90 days.</summary>
    private async Task<(User User, Printer Printer)> SilentAsync(TimeSpan ago, int earlier = 2, PrintSource source = PrintSource.Moonraker, string name = "Voron 2.4", bool active = true)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db);
        var printer = await CampaignTestData.PrinterAsync(db, user.Id, name, active);
        var last = Now - ago;
        await CampaignTestData.PrintAsync(db, user.Id, printer.Id, last, source);
        for (var i = 1; i <= earlier; i++)
        {
            await CampaignTestData.PrintAsync(db, user.Id, printer.Id, last.AddDays(-10 * i), source);
        }
        return (user, printer);
    }

    private async Task<List<DueEmail>> DueForAsync(long userId, DateTimeOffset? now = null)
    {
        using var scope = _factory.Services.CreateScope();
        return (await Campaign(scope).FindDueAsync(now ?? Now, Ct)).Where(d => d.UserId == userId).ToList();
    }

    [Fact]
    public void Contract()
    {
        using var scope = _factory.Services.CreateScope();
        var campaign = Campaign(scope);
        Assert.Equal("printer-silent", campaign.Name);
        Assert.Equal(EmailSettingTypes.PrinterSilent, campaign.PreferenceSettingTypeId);
        Assert.False(campaign.ExemptFromFrequencyCap);
        Assert.True(campaign.CountsTowardFrequencyCap);
        Assert.Equal(TimeSpan.FromDays(3), campaign.Lifetime);
    }

    [Theory]
    [InlineData(14 * 24, true)]
    [InlineData(14 * 24 - 1, false)]
    [InlineData(21 * 24, true)]
    [InlineData(21 * 24 + 1, false)]
    public async Task WindowEdges(int hoursAgo, bool due)
    {
        var (user, _) = await SilentAsync(TimeSpan.FromHours(hoursAgo));
        Assert.Equal(due, (await DueForAsync(user.Id)).Count == 1);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task NeedsThreeAutomatedPrintsInNinetyDays(int earlier, bool due)
    {
        var (user, _) = await SilentAsync(TimeSpan.FromDays(15), earlier);
        Assert.Equal(due, (await DueForAsync(user.Id)).Count == 1);
    }

    [Fact]
    public async Task ManualPrintAfterwards_DoesNotResetSilence()
    {
        var (user, printer) = await SilentAsync(TimeSpan.FromDays(15));
        using (var scope = _factory.Services.CreateScope())
        {
            await CampaignTestData.PrintAsync(scope.ServiceProvider.GetRequiredService<PrintLogContext>(), user.Id, printer.Id, Now.AddDays(-1), PrintSource.Web);
        }

        Assert.Single(await DueForAsync(user.Id));
    }

    [Fact]
    public async Task AutomatedPrintOnAnotherPrinter_DoesNotResetThisOne()
    {
        var (user, _) = await SilentAsync(TimeSpan.FromDays(15));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var other = await CampaignTestData.PrinterAsync(db, user.Id, "Ender 3");
            await CampaignTestData.PrintAsync(db, user.Id, other.Id, Now.AddDays(-1), PrintSource.OctoPrint);
        }

        Assert.Single(await DueForAsync(user.Id));
    }

    [Fact]
    public async Task InactivePrinter_NotDue()
    {
        var (user, _) = await SilentAsync(TimeSpan.FromDays(15), active: false);
        Assert.Empty(await DueForAsync(user.Id));
    }

    [Fact]
    public async Task TwoSilentPrinters_OneEmailKeyedOnTheLaterSilence()
    {
        var (user, _) = await SilentAsync(TimeSpan.FromDays(20));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var second = await CampaignTestData.PrinterAsync(db, user.Id, "Bambu X1");
            foreach (var daysAgo in new[] { 15, 25, 35 })
            {
                await CampaignTestData.PrintAsync(db, user.Id, second.Id, Now.AddDays(-daysAgo), PrintSource.SlicerPlugin);
            }
        }

        var due = Assert.Single(await DueForAsync(user.Id));
        Assert.Equal($"silent:{Now.AddDays(-15):yyyy-MM-dd}", due.PeriodKey);
    }

    [Fact]
    public async Task RecentAlert_SuppressesANewOne()
    {
        var (user, _) = await SilentAsync(TimeSpan.FromDays(15));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            db.EmailOutbox.Add(EmailTestData.OutboxRow(user.Id, "printer-silent", "silent:2026-10-30", Now.AddDays(-10)));
            await db.SaveChangesAsync(Ct);
        }

        Assert.Empty(await DueForAsync(user.Id));
    }

    [Fact]
    public async Task SendAfter_IsTheNextNineAmInTheUsersZone()
    {
        var (user, _) = await SilentAsync(TimeSpan.FromDays(15));
        using (var scope = _factory.Services.CreateScope())
        {
            await CampaignTestData.SetZoneAsync(scope.ServiceProvider.GetRequiredService<PrintLogContext>(), user.Id, "Europe/Berlin");
        }

        // 15:00Z is 16:00 in Berlin, so the next 09:00 there is tomorrow at 08:00Z.
        Assert.Equal(new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.Zero), Assert.Single(await DueForAsync(user.Id)).SendAfter);
    }

    // ---------- rendering ----------

    private async Task<RenderedEmail?> RenderAsync(long userId)
    {
        var due = Assert.Single(await DueForAsync(userId));
        using var scope = _factory.Services.CreateScope();
        return await Campaign(scope).RenderAsync(EmailTestData.OutboxRow(userId, "printer-silent", due.PeriodKey, Now), Ct);
    }

    [Fact]
    public async Task Render_SinglePrinter()
    {
        var (user, printer) = await SilentAsync(TimeSpan.FromDays(15), source: PrintSource.Moonraker);

        var email = await RenderAsync(user.Id);

        Assert.NotNull(email);
        Assert.Equal("Did your Voron 2.4 stop reporting?", email.Subject);
        Assert.Contains($"Last heard from via Moonraker on {(Now - TimeSpan.FromDays(15)).ToOffset(TimeSpan.FromHours(-6)):MMM d}", email.Text);
        Assert.Contains("/docs/klipper?utm_source=email", email.Text);
        Assert.Contains($"/printers/{printer.Id}?utm_source=email", email.Text);
        Assert.Contains("Quiet 15 days", email.Text);
        Assert.Contains("Quiet 15 days", email.Html);
        Assert.Contains("#ffb300", email.Html);
        Assert.DoesNotContain("going Pro", email.Html);
        Assert.DoesNotContain("Thanks for supporting", email.Html);
        Assert.Equal(EmailSettingTypes.PrinterSilent, email.UnsubscribeCategory);
        var listed = Assert.Single(email.Exposure["printers"]!.AsArray());
        Assert.Equal(printer.Id, (long?)listed!["id"]);
        Assert.Equal("Moonraker", (string?)listed["source"]);
    }

    [Theory]
    [InlineData(PrintSource.OctoPrint, "/docs/octoprint-webhook")]
    [InlineData(PrintSource.SlicerPlugin, "/docs/slic3r-uploader")]
    [InlineData(PrintSource.ApiKey, "/api-keys")]
    public async Task Render_TroubleshootingLinkFollowsTheSource(PrintSource source, string path)
    {
        var (user, _) = await SilentAsync(TimeSpan.FromDays(15), source: source);

        var email = await RenderAsync(user.Id);

        Assert.Contains($"{path}?utm_source=email", email!.Text);
    }

    [Fact]
    public async Task Render_DropsAPrinterThatSpokeUpAgain()
    {
        var (user, quiet) = await SilentAsync(TimeSpan.FromDays(16), name: "Quiet One");
        Printer recovered;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            recovered = await CampaignTestData.PrinterAsync(db, user.Id, "Back Again");
            foreach (var daysAgo in new[] { 15, 25, 35 })
            {
                await CampaignTestData.PrintAsync(db, user.Id, recovered.Id, Now.AddDays(-daysAgo), PrintSource.OctoPrint);
            }
        }

        var due = Assert.Single(await DueForAsync(user.Id));
        using (var scope = _factory.Services.CreateScope())
        {
            await CampaignTestData.PrintAsync(scope.ServiceProvider.GetRequiredService<PrintLogContext>(), user.Id, recovered.Id, Now.AddHours(-1), PrintSource.OctoPrint);
        }

        using var renderScope = _factory.Services.CreateScope();
        var email = await Campaign(renderScope).RenderAsync(EmailTestData.OutboxRow(user.Id, "printer-silent", due.PeriodKey, Now), Ct);

        Assert.NotNull(email);
        Assert.Equal("Did your Quiet One stop reporting?", email.Subject);
        Assert.DoesNotContain("Back Again", email.Text);
    }

    [Fact]
    public async Task Render_NullWhenEveryPrinterSpokeUp()
    {
        var (user, printer) = await SilentAsync(TimeSpan.FromDays(15));
        var due = Assert.Single(await DueForAsync(user.Id));
        using (var scope = _factory.Services.CreateScope())
        {
            await CampaignTestData.PrintAsync(scope.ServiceProvider.GetRequiredService<PrintLogContext>(), user.Id, printer.Id, Now.AddHours(-1), PrintSource.Moonraker);
        }

        using var renderScope = _factory.Services.CreateScope();
        Assert.Null(await Campaign(renderScope).RenderAsync(EmailTestData.OutboxRow(user.Id, "printer-silent", due.PeriodKey, Now), Ct));
    }

    [Fact]
    public async Task Render_SeveralPrinters_PluralSubject()
    {
        var (user, _) = await SilentAsync(TimeSpan.FromDays(20));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var second = await CampaignTestData.PrinterAsync(db, user.Id, "Bambu X1");
            foreach (var daysAgo in new[] { 15, 25, 35 })
            {
                await CampaignTestData.PrintAsync(db, user.Id, second.Id, Now.AddDays(-daysAgo), PrintSource.SlicerPlugin);
            }
        }

        Assert.Equal("2 of your printers stopped reporting", (await RenderAsync(user.Id))!.Subject);
    }

    [Fact]
    public async Task Render_ApiKeyNote_OnlyWhenNewestKeyUseIsOlderThanSilence()
    {
        var (stale, _) = await SilentAsync(TimeSpan.FromDays(15), source: PrintSource.ApiKey);
        var (fresh, _) = await SilentAsync(TimeSpan.FromDays(15), source: PrintSource.ApiKey);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            db.UserApiKeys.Add(ApiKey(stale.Id, Now.AddDays(-30)));
            db.UserApiKeys.Add(ApiKey(fresh.Id, Now.AddDays(-1)));
            await db.SaveChangesAsync(Ct);
        }

        Assert.Contains("API key was last used", (await RenderAsync(stale.Id))!.Text);
        Assert.DoesNotContain("API key was last used", (await RenderAsync(fresh.Id))!.Text);
    }

    private static UserApiKey ApiKey(long userId, DateTimeOffset lastUsed) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Description = "printer",
        LastUsed = lastUsed,
        HashedKey = Guid.NewGuid().ToString("N"),
        HashAlgorithm = "test",
        CreatedById = userId,
        UpdatedById = userId,
    };

    // ---------- SQL Server shape ----------

    // Stage 1 must stay a plain filtered projection (no aggregates), the shape that avoids
    // SQL Server error 8124 and that the SQLite suite cannot vet.
    [Fact]
    public void Stage1_TranslatesWithoutAggregates()
    {
        using var context = new PrintLogContext(new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlServer("Server=unused;Database=unused;Trusted_Connection=True;")
            .Options);

        var sql = PrinterSilentCampaign.AutomatedPrints(context, Now.UtcDateTime.AddDays(-111)).ToQueryString();

        Assert.DoesNotContain("MAX(", sql);
        Assert.DoesNotContain("COUNT(", sql);
        Assert.Contains("[IsActive]", sql);
    }

    // ---------- golden ----------

    [Fact]
    public async Task Golden()
    {
        var renderer = _factory.Services.GetRequiredService<IEmailTemplateRenderer>();
        var (html, text) = await PrinterSilentTemplates.RenderAsync(renderer, GoldenModel(), CampaignTestData.Footer);

        GoldenFile.AssertMatches(html, "printer-silent.approved.html");
        GoldenFile.AssertMatches(text, "printer-silent.approved.txt");
    }

    // Three printers, one with a long name, so wrapping and the card layout get exercised.
    internal static PrinterSilentModel GoldenModel() => new(
        Name: "Ada",
        Printers:
        [
            new SilentPrinterView("Voron 2.4", "LDO Voron 2.4", "Moonraker", "Oct 2", "https://www.3dprintlog.test/docs/klipper", "Klipper setup guide", "https://www.3dprintlog.test/printers/7", 18),
            new SilentPrinterView("Bambu X1", null, "the slicer uploader", "Oct 5", "https://www.3dprintlog.test/docs/slic3r-uploader", "Slicer uploader guide", "https://www.3dprintlog.test/printers/9", 15),
            new SilentPrinterView("Prusa MK4S in the garage, the one with the long enclosure name", "Prusa Research MK4S", "OctoPrint", "Oct 6", "https://www.3dprintlog.test/docs/octoprint-webhook", "OctoPrint setup guide", "https://www.3dprintlog.test/printers/11", 14),
        ],
        ApiKeyLastUsed: "Sep 28");
}
