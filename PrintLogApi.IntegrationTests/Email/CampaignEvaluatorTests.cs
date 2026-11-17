using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Email;
using PrintLogApi.Email.Campaigns;
using PrintLogApi.Email.Outbox;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email;

public class CampaignEvaluatorTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly TelemetryClient Telemetry = new(new TelemetryConfiguration());

    private readonly CustomWebApplicationFactory _factory;

    public CampaignEvaluatorTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static EmailOptions Options(bool enabled = true, bool dryRun = false, params string[] enabledCampaigns)
    {
        var options = new EmailOptions { Enabled = enabled, DryRun = dryRun };
        foreach (var name in enabledCampaigns)
        {
            options.Campaigns[EmailOptions.CampaignKey(name)] = new CampaignOptions { Enabled = true };
        }
        return options;
    }

    private static CampaignEvaluator Evaluator(PrintLogContext db, EmailOptions options, params IEmailCampaign[] campaigns)
        => new(db, campaigns, Microsoft.Extensions.Options.Options.Create(options), Telemetry);

    private static Task<List<EmailOutbox>> RowsAsync(PrintLogContext db, long userId)
        => db.EmailOutbox.AsNoTracking().Where(o => o.UserId == userId).ToListAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task RunOnce_InsertsPendingRowsWithTimes()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db);
        var campaign = new TestCampaign("eval-times");
        var sendAfter = Now.AddHours(3);
        campaign.Due.Add(new DueEmail(user.Id, "p1", sendAfter));

        var inserted = await Evaluator(db, Options(enabledCampaigns: campaign.Name), campaign).RunOnceAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(1, inserted);
        var row = Assert.Single(await RowsAsync(db, user.Id));
        Assert.Equal(EmailOutboxStatus.Pending, row.Status);
        Assert.Equal("eval-times", row.Campaign);
        Assert.Equal("p1", row.PeriodKey);
        Assert.Equal(sendAfter, row.SendAfter);
        Assert.Equal(sendAfter, row.NextAttemptAt);
        Assert.Equal(sendAfter.AddDays(2), row.ExpiresAt);
        Assert.Equal(Now, row.CreatedAt);
        Assert.Equal(0, row.Attempts);
    }

    [Fact]
    public async Task RunOnce_TwiceInsertsOnce()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db);
        var campaign = new TestCampaign("eval-twice");
        campaign.Due.Add(new DueEmail(user.Id, "p1", Now));
        var evaluator = Evaluator(db, Options(enabledCampaigns: campaign.Name), campaign);

        await evaluator.RunOnceAsync(Now, TestContext.Current.CancellationToken);
        var second = await evaluator.RunOnceAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(0, second);
        Assert.Single(await RowsAsync(db, user.Id));
    }

    [Fact]
    public async Task RunOnce_DuplicateDueEntries_InsertOnce()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db);
        var campaign = new TestCampaign("eval-dupes");
        campaign.Due.Add(new DueEmail(user.Id, "p1", Now));
        campaign.Due.Add(new DueEmail(user.Id, "p1", Now.AddHours(1)));

        Assert.Equal(1, await Evaluator(db, Options(enabledCampaigns: campaign.Name), campaign).RunOnceAsync(Now, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunOnce_SkipsDisabledCampaign()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db);
        var campaign = new TestCampaign("eval-disabled");
        campaign.Due.Add(new DueEmail(user.Id, "p1", Now));

        Assert.Equal(0, await Evaluator(db, Options(), campaign).RunOnceAsync(Now, TestContext.Current.CancellationToken));
        Assert.Empty(await RowsAsync(db, user.Id));
    }

    [Fact]
    public async Task RunOnce_DoesNothingWhenEmailDisabledAndNotDryRun()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db);
        var campaign = new TestCampaign("eval-off");
        campaign.Due.Add(new DueEmail(user.Id, "p1", Now));

        Assert.Equal(0, await Evaluator(db, Options(enabled: false, enabledCampaigns: campaign.Name), campaign).RunOnceAsync(Now, TestContext.Current.CancellationToken));
        Assert.Equal(1, await Evaluator(db, Options(enabled: false, dryRun: true, enabledCampaigns: campaign.Name), campaign).RunOnceAsync(Now, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunOnce_ContinuesAfterOneCampaignThrows()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db);
        var broken = new TestCampaign("eval-broken") { ThrowOnFind = new InvalidOperationException("boom") };
        var good = new TestCampaign("eval-good");
        good.Due.Add(new DueEmail(user.Id, "p1", Now));

        var inserted = await Evaluator(db, Options(enabledCampaigns: [broken.Name, good.Name]), broken, good)
            .RunOnceAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(1, inserted);
        Assert.Equal("eval-good", Assert.Single(await RowsAsync(db, user.Id)).Campaign);
    }

    // The existence check and the insert are not atomic. A row inserted between them (another
    // instance, a manual fix) makes the batch hit the unique index; the evaluator must fall back
    // to row-by-row and still insert everything else.
    [Fact]
    public async Task RunOnce_RecoversFromDuplicateInBatch()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var racer = await EmailTestData.CreateUserAsync(db);
        var other = await EmailTestData.CreateUserAsync(db);
        var campaign = new TestCampaign("eval-race");
        campaign.Due.Add(new DueEmail(racer.Id, "p1", Now));
        campaign.Due.Add(new DueEmail(other.Id, "p1", Now));

        var evaluator = Evaluator(db, Options(enabledCampaigns: campaign.Name), campaign);
        evaluator.BeforeInsertForTesting = async token =>
        {
            using var raceScope = _factory.Services.CreateScope();
            var raceDb = raceScope.ServiceProvider.GetRequiredService<PrintLogContext>();
            raceDb.EmailOutbox.Add(EmailTestData.OutboxRow(racer.Id, campaign.Name, "p1", Now));
            await raceDb.SaveChangesAsync(token);
        };

        var inserted = await evaluator.RunOnceAsync(Now, ct);

        Assert.Equal(1, inserted);
        Assert.Single(await RowsAsync(db, racer.Id));
        Assert.Single(await RowsAsync(db, other.Id));
    }
}
