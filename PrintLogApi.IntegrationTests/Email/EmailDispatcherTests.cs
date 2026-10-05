using System.Text.Json.Nodes;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Email;
using PrintLogApi.Email.Campaigns;
using PrintLogApi.Email.Outbox;
using PrintLogApi.Email.Templates;
using PrintLogApi.Email.Tokens;
using PrintLogApi.Email.Transport;
using PrintLogApi.IntegrationTests.Analytics;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email;

public class EmailDispatcherTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string Key = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);

    private readonly CustomWebApplicationFactory _factory;

    public EmailDispatcherTests(CustomWebApplicationFactory factory) => _factory = factory;

    /// <summary>One test's world: its own scope, campaigns, transport, options and clock.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly IServiceScope _scope;

        public Harness(CustomWebApplicationFactory factory, params IEmailCampaign[] campaigns)
        {
            _scope = factory.Services.CreateScope();
            Db = _scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            // A tick claims every due row in the database, and this class's tests share one.
            // Starting from an empty outbox keeps each test independent of what ran before it.
            Db.EmailOutbox.ExecuteDelete();

            Campaigns = campaigns.Length > 0 ? [.. campaigns] : [new TestCampaign("dispatch-test")];
            Options = new EmailOptions
            {
                Enabled = true,
                HoldoutPercent = 0,
                MaxSendsPerSecond = 1000,
                Notice = new NoticeOptions { Required = false },
                UnsubscribeSigningKeys = [Key],
                SuppressionPeppers = [Key],
                WebBaseUrl = "https://www.3dprintlog.test",
                ApiBaseUrl = "https://api.3dprintlog.test",
            };
            foreach (var c in Campaigns)
            {
                Options.Campaigns[EmailOptions.CampaignKey(c.Name)] = new CampaignOptions { Enabled = true };
            }
            Hasher = new EmailAddressHasher(Microsoft.Extensions.Options.Options.Create(Options));
        }

        public PrintLogContext Db { get; }
        public List<IEmailCampaign> Campaigns { get; }
        public TestCampaign Campaign => (TestCampaign)Campaigns[0];
        public EmailOptions Options { get; }
        public FakeEmailTransport Transport { get; } = new();
        public SettableTimeProvider Clock { get; } = new(Now);
        public EmailAddressHasher Hasher { get; }
        public CancellationToken Ct => TestContext.Current.CancellationToken;

        public EmailDispatcher Dispatcher(PrintLogContext? db = null)
        {
            var context = db ?? Db;
            var options = Microsoft.Extensions.Options.Options.Create(Options);
            var telemetry = new TelemetryClient(new TelemetryConfiguration());
            return new EmailDispatcher(
                context,
                Campaigns,
                new EmailPreferenceService(context, telemetry),
                Hasher,
                Transport,
                new EmailTokenService(options, Clock),
                new EmailLinkBuilder(options),
                options,
                Clock,
                telemetry);
        }

        public Task<DispatchTickResult> TickAsync() => Dispatcher().RunOnceAsync(Clock.GetUtcNow(), Ct);

        public async Task<User> UserAsync(bool verified = true) => await EmailTestData.CreateUserAsync(Db, verified: verified);

        public async Task<EmailOutbox> QueueAsync(long userId, string? campaign = null, string period = "p1", DateTimeOffset? due = null)
        {
            var row = EmailTestData.OutboxRow(userId, campaign ?? Campaigns[0].Name, period, due ?? Now);
            Db.EmailOutbox.Add(row);
            await Db.SaveChangesAsync(Ct);
            Db.ChangeTracker.Clear();
            return row;
        }

        public async Task<EmailOutbox> ReloadAsync(long id) => await Db.EmailOutbox.AsNoTracking().SingleAsync(o => o.Id == id, Ct);

        public void Dispose() => _scope.Dispose();
    }

    private static void AssertSkipped(EmailOutbox row, string reason)
    {
        Assert.Equal(EmailOutboxStatus.Skipped, row.Status);
        Assert.Equal(reason, row.SkipReason);
    }

    // ---------- gates, in spec order ----------

    [Fact]
    public async Task Deactivating_Skips()
    {
        using var h = new Harness(_factory);
        var user = await h.UserAsync();
        await h.Db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DeactivationDateTime, Now), h.Ct);
        var row = await h.QueueAsync(user.Id);

        await h.TickAsync();

        AssertSkipped(await h.ReloadAsync(row.Id), EmailSkipReasons.Deactivating);
    }

    [Fact]
    public async Task DisabledCampaign_Skips()
    {
        using var h = new Harness(_factory);
        h.Options.Campaigns[EmailOptions.CampaignKey(h.Campaign.Name)].Enabled = false;
        var row = await h.QueueAsync((await h.UserAsync()).Id);

        await h.TickAsync();

        AssertSkipped(await h.ReloadAsync(row.Id), EmailSkipReasons.DisabledCampaign);
    }

    // A row left behind by a campaign that no longer exists in code.
    [Fact]
    public async Task UnknownCampaign_SkipsAsDisabled()
    {
        using var h = new Harness(_factory);
        var row = await h.QueueAsync((await h.UserAsync()).Id, campaign: "retired-campaign");

        await h.TickAsync();

        AssertSkipped(await h.ReloadAsync(row.Id), EmailSkipReasons.DisabledCampaign);
    }

    [Fact]
    public async Task Unverified_Skips()
    {
        using var h = new Harness(_factory);
        var row = await h.QueueAsync((await h.UserAsync(verified: false)).Id);

        await h.TickAsync();

        AssertSkipped(await h.ReloadAsync(row.Id), EmailSkipReasons.Unverified);
    }

    [Fact]
    public async Task NoAddress_SkipsAsUnverified()
    {
        using var h = new Harness(_factory);
        var user = await h.UserAsync();
        await h.Db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Email, (string?)null), h.Ct);
        var row = await h.QueueAsync(user.Id);

        await h.TickAsync();

        AssertSkipped(await h.ReloadAsync(row.Id), EmailSkipReasons.Unverified);
    }

    [Fact]
    public async Task Suppressed_Skips()
    {
        using var h = new Harness(_factory);
        var user = await h.UserAsync();
        h.Db.EmailSuppressions.Add(new EmailSuppression { EmailHash = h.Hasher.Hash(user.Email!), Reason = EmailSuppressionReason.Complaint, CreatedAt = Now });
        await h.Db.SaveChangesAsync(h.Ct);
        var row = await h.QueueAsync(user.Id);

        await h.TickAsync();

        AssertSkipped(await h.ReloadAsync(row.Id), EmailSkipReasons.Suppressed);
        Assert.Empty(h.Transport.Sent);
    }

    [Theory]
    [InlineData(EmailSettingTypes.MonthlyRecap)]
    [InlineData(EmailSettingTypes.All)]
    public async Task OptedOut_Skips(int settingType)
    {
        using var h = new Harness(_factory);
        var user = await h.UserAsync();
        await new EmailPreferenceService(h.Db, new TelemetryClient(new TelemetryConfiguration())).SetAsync(user.Id, settingType, false, h.Ct);
        var row = await h.QueueAsync(user.Id);

        await h.TickAsync();

        AssertSkipped(await h.ReloadAsync(row.Id), EmailSkipReasons.OptedOut);
    }

    [Fact]
    public async Task NoticeRequired_DefersUntilSeen()
    {
        using var h = new Harness(_factory);
        h.Options.Notice.Required = true;
        var user = await h.UserAsync();
        var row = await h.QueueAsync(user.Id);

        await h.TickAsync();

        var deferred = await h.ReloadAsync(row.Id);
        Assert.Equal(EmailOutboxStatus.Pending, deferred.Status);
        Assert.Equal(Now.AddHours(1), deferred.NextAttemptAt);
        Assert.Empty(h.Transport.Sent);

        h.Db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            UserSettingTypeId = EmailSettingTypes.NoticeSeenAt,
            Value = Now.ToString("O"),
            CreatedById = user.Id,
            UpdatedById = user.Id,
        });
        await h.Db.SaveChangesAsync(h.Ct);
        h.Clock.SetUtcNow(Now.AddHours(1));

        await h.TickAsync();

        Assert.Equal(EmailOutboxStatus.Sent, (await h.ReloadAsync(row.Id)).Status);
    }

    [Fact]
    public async Task AllowList_DefersOthers_SendsListed()
    {
        using var h = new Harness(_factory);
        var listed = await h.UserAsync();
        var other = await h.UserAsync();
        h.Options.AllowListUserIds = [listed.Id];
        var listedRow = await h.QueueAsync(listed.Id);
        var otherRow = await h.QueueAsync(other.Id);

        await h.TickAsync();

        Assert.Equal(EmailOutboxStatus.Sent, (await h.ReloadAsync(listedRow.Id)).Status);
        var deferred = await h.ReloadAsync(otherRow.Id);
        Assert.Equal(EmailOutboxStatus.Pending, deferred.Status);
        Assert.Equal(Now.AddHours(1), deferred.NextAttemptAt);
    }

    // Review Focus 2.
    [Fact]
    public async Task FrequencyCap_DefersSecondCampaign()
    {
        var capped = new TestCampaign("cap-recap");
        var other = new TestCampaign("cap-silent");
        var onboarding = new TestCampaign("cap-onboarding") { ExemptFromFrequencyCap = true };
        using var h = new Harness(_factory, capped, other, onboarding);
        var user = await h.UserAsync();
        var lastSent = Now.AddDays(-1);
        var sent = EmailTestData.OutboxRow(user.Id, capped.Name, "earlier", lastSent);
        sent.Status = EmailOutboxStatus.Sent;
        sent.SentAt = lastSent;
        h.Db.EmailOutbox.Add(sent);
        await h.Db.SaveChangesAsync(h.Ct);
        var cappedRow = await h.QueueAsync(user.Id, campaign: other.Name);

        await h.TickAsync();

        var deferred = await h.ReloadAsync(cappedRow.Id);
        Assert.Equal(EmailOutboxStatus.Pending, deferred.Status);
        Assert.Equal(lastSent.AddDays(3), deferred.NextAttemptAt);

        var onboardingRow = await h.QueueAsync(user.Id, campaign: onboarding.Name);
        await h.TickAsync();
        Assert.Equal(EmailOutboxStatus.Sent, (await h.ReloadAsync(onboardingRow.Id)).Status);
    }

    // A second dispatcher (slot swap, another instance) holds this user's other row in Sending.
    // The cap only counts Sent rows, so without this the two would go out together.
    [Fact]
    public async Task FrequencyCap_DefersWhileAnotherRowForTheUserIsInFlight()
    {
        var recap = new TestCampaign("flight-recap");
        var silent = new TestCampaign("flight-silent");
        using var h = new Harness(_factory, recap, silent);
        var user = await h.UserAsync();
        var inFlight = EmailTestData.OutboxRow(user.Id, recap.Name, "in-flight", Now);
        inFlight.Status = EmailOutboxStatus.Sending;
        inFlight.ClaimedAt = Now.AddSeconds(-5);
        h.Db.EmailOutbox.Add(inFlight);
        await h.Db.SaveChangesAsync(h.Ct);
        var row = await h.QueueAsync(user.Id, campaign: silent.Name);

        await h.TickAsync();

        var deferred = await h.ReloadAsync(row.Id);
        Assert.Equal(EmailOutboxStatus.Pending, deferred.Status);
        Assert.Equal(Now.AddMinutes(1), deferred.NextAttemptAt);
    }

    [Fact]
    public async Task TwoDueRowsSameUser_OneSentOneDeferred()
    {
        var recap = new TestCampaign("pair-recap");
        var silent = new TestCampaign("pair-silent");
        using var h = new Harness(_factory, recap, silent);
        var user = await h.UserAsync();
        var first = await h.QueueAsync(user.Id, campaign: recap.Name);
        var second = await h.QueueAsync(user.Id, campaign: silent.Name);

        var tick1 = await h.TickAsync();

        Assert.Equal(1, tick1.Claimed);
        Assert.Equal(EmailOutboxStatus.Sent, (await h.ReloadAsync(first.Id)).Status);
        Assert.Equal(EmailOutboxStatus.Pending, (await h.ReloadAsync(second.Id)).Status);

        await h.TickAsync();

        var deferred = await h.ReloadAsync(second.Id);
        Assert.Equal(EmailOutboxStatus.Pending, deferred.Status);
        Assert.Equal(Now.AddDays(3), deferred.NextAttemptAt);
        Assert.Single(h.Transport.Sent);
    }

    [Fact]
    public async Task RenderNull_SkipsNotRelevant()
    {
        using var h = new Harness(_factory);
        h.Campaign.RenderNull = true;
        var row = await h.QueueAsync((await h.UserAsync()).Id);

        await h.TickAsync();

        AssertSkipped(await h.ReloadAsync(row.Id), EmailSkipReasons.NotRelevant);
    }

    [Fact]
    public async Task Holdout_AfterRender()
    {
        using var h = new Harness(_factory);
        h.Options.HoldoutPercent = 100;
        var row = await h.QueueAsync((await h.UserAsync()).Id);

        await h.TickAsync();

        var held = await h.ReloadAsync(row.Id);
        AssertSkipped(held, EmailSkipReasons.Holdout);
        Assert.Contains(row.Id, h.Campaign.RenderedRowIds);
        var exposure = JsonNode.Parse(held.Exposure!)!;
        Assert.Equal("holdout", (string?)exposure["arm"]);
        Assert.Equal(row.Id, (long?)exposure["row"]);
        Assert.Empty(h.Transport.Sent);
    }

    [Fact]
    public async Task Holdout_NotRelevantWins()
    {
        using var h = new Harness(_factory);
        h.Options.HoldoutPercent = 100;
        h.Campaign.RenderNull = true;
        var row = await h.QueueAsync((await h.UserAsync()).Id);

        await h.TickAsync();

        AssertSkipped(await h.ReloadAsync(row.Id), EmailSkipReasons.NotRelevant);
    }

    [Fact]
    public async Task DryRun_RendersAndSkips()
    {
        using var h = new Harness(_factory);
        h.Options.Enabled = false;
        h.Options.DryRun = true;
        var row = await h.QueueAsync((await h.UserAsync()).Id);

        await h.TickAsync();

        AssertSkipped(await h.ReloadAsync(row.Id), EmailSkipReasons.DryRun);
        Assert.Contains(row.Id, h.Campaign.RenderedRowIds);
        Assert.Empty(h.Transport.Sent);
    }

    [Fact]
    public async Task Inactive_DoesNothing()
    {
        using var h = new Harness(_factory);
        h.Options.Enabled = false;
        var row = await h.QueueAsync((await h.UserAsync()).Id);

        Assert.Equal(new DispatchTickResult(0, 0, 0, 0, 0, false), await h.TickAsync());
        Assert.Equal(EmailOutboxStatus.Pending, (await h.ReloadAsync(row.Id)).Status);
    }

    [Fact]
    public async Task NotYetDue_IsNotClaimed()
    {
        using var h = new Harness(_factory);
        var row = await h.QueueAsync((await h.UserAsync()).Id, due: Now.AddMinutes(1));

        Assert.Equal(0, (await h.TickAsync()).Claimed);
        Assert.Equal(EmailOutboxStatus.Pending, (await h.ReloadAsync(row.Id)).Status);
    }

    // ---------- sending ----------

    [Fact]
    public async Task Send_Success_RecordsIdsAndHash()
    {
        using var h = new Harness(_factory);
        var user = await h.UserAsync();
        var row = await h.QueueAsync(user.Id);
        h.Transport.NextMessageId = "ses-123";

        var result = await h.TickAsync();

        Assert.Equal(1, result.Sent);
        var sent = await h.ReloadAsync(row.Id);
        Assert.Equal(EmailOutboxStatus.Sent, sent.Status);
        Assert.Equal("ses-123", sent.ProviderMessageId);
        Assert.Equal(h.Hasher.Hash(user.Email!), sent.SentTo);
        Assert.Equal(Now, sent.SentAt);
        Assert.Equal("treatment", (string?)JsonNode.Parse(sent.Exposure!)!["arm"]);

        var message = Assert.Single(h.Transport.Sent);
        Assert.Equal(user.Email, message.ToAddress);
        Assert.Equal(row.Id, message.OutboxId);
        Assert.Equal(h.Campaign.Name, message.Campaign);
        Assert.StartsWith("<https://api.3dprintlog.test/api/email/unsubscribe?t=", message.Headers["List-Unsubscribe"]);
        Assert.Equal("List-Unsubscribe=One-Click", message.Headers["List-Unsubscribe-Post"]);
    }

    [Theory]
    [InlineData(EmailSendFailureKind.Throttled)]
    [InlineData(EmailSendFailureKind.NotConnected)]
    public async Task RetryableFailure_BacksOffThenFails(EmailSendFailureKind kind)
    {
        using var h = new Harness(_factory);
        var row = await h.QueueAsync((await h.UserAsync()).Id);
        TimeSpan[] schedule = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2)];
        var now = Now;

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            h.Transport.Throws.Enqueue(new EmailSendException(kind, new Exception("x")));
            h.Clock.SetUtcNow(now);
            await h.TickAsync();

            var retry = await h.ReloadAsync(row.Id);
            Assert.Equal(EmailOutboxStatus.Pending, retry.Status);
            Assert.Equal(attempt, retry.Attempts);
            Assert.Equal(now + schedule[attempt - 1], retry.NextAttemptAt);
            now = retry.NextAttemptAt;
        }

        h.Transport.Throws.Enqueue(new EmailSendException(kind, new Exception("x")));
        h.Clock.SetUtcNow(now);
        await h.TickAsync();

        var failed = await h.ReloadAsync(row.Id);
        Assert.Equal(EmailOutboxStatus.Failed, failed.Status);
        Assert.Equal(5, failed.Attempts);
    }

    [Fact]
    public async Task Rejected_FailsImmediately()
    {
        using var h = new Harness(_factory);
        var row = await h.QueueAsync((await h.UserAsync()).Id);
        h.Transport.Throws.Enqueue(new EmailSendException(EmailSendFailureKind.Rejected, new Exception("bad address")));

        var result = await h.TickAsync();

        Assert.Equal(1, result.Failed);
        Assert.Equal(EmailOutboxStatus.Failed, (await h.ReloadAsync(row.Id)).Status);
    }

    [Fact]
    public async Task AccountPaused_ReturnsRowAndStopsTick()
    {
        using var h = new Harness(_factory);
        var first = await h.QueueAsync((await h.UserAsync()).Id, due: Now.AddMinutes(-2));
        var second = await h.QueueAsync((await h.UserAsync()).Id, due: Now.AddMinutes(-1));
        h.Transport.Throws.Enqueue(new EmailSendException(EmailSendFailureKind.AccountPaused, new Exception("paused")));

        var result = await h.TickAsync();

        Assert.True(result.Paused);
        var returned = await h.ReloadAsync(first.Id);
        Assert.Equal(EmailOutboxStatus.Pending, returned.Status);
        Assert.Equal(0, returned.Attempts);
        Assert.Null(returned.ClaimedAt);
        Assert.Equal(EmailOutboxStatus.Pending, (await h.ReloadAsync(second.Id)).Status);
        Assert.Empty(h.Transport.Sent);
    }

    [Fact]
    public async Task Ambiguous_FailsAndIsNeverRetried()
    {
        using var h = new Harness(_factory);
        var row = await h.QueueAsync((await h.UserAsync()).Id);
        h.Transport.Throws.Enqueue(new EmailSendException(EmailSendFailureKind.Ambiguous, new TaskCanceledException("timeout")));

        await h.TickAsync();
        h.Clock.SetUtcNow(Now.AddDays(1));
        await h.TickAsync();

        var failed = await h.ReloadAsync(row.Id);
        Assert.Equal(EmailOutboxStatus.Failed, failed.Status);
        Assert.StartsWith("ambiguous:", failed.LastError);
        Assert.Empty(h.Transport.Sent);
    }

    // A transport that breaks its contract (a raw exception) gets the safe reading: it may have sent.
    [Fact]
    public async Task UnclassifiedTransportException_IsAmbiguous()
    {
        using var h = new Harness(_factory);
        var row = await h.QueueAsync((await h.UserAsync()).Id);
        h.Transport.Throws.Enqueue(new InvalidOperationException("surprise"));

        await h.TickAsync();

        Assert.StartsWith("ambiguous:", (await h.ReloadAsync(row.Id)).LastError);
    }

    // Nothing was sent when rendering throws, so the row is retried rather than left to be
    // reaped as an ambiguous abandoned claim.
    [Fact]
    public async Task RenderThrows_RetriesLater()
    {
        using var h = new Harness(_factory, new ThrowingRenderCampaign());
        var row = await h.QueueAsync((await h.UserAsync()).Id);

        await h.TickAsync();

        var retry = await h.ReloadAsync(row.Id);
        Assert.Equal(EmailOutboxStatus.Pending, retry.Status);
        Assert.Equal(1, retry.Attempts);
        Assert.Equal(Now.AddMinutes(1), retry.NextAttemptAt);
    }

    // ---------- housekeeping ----------

    [Fact]
    public async Task AbandonedClaim_BecomesAmbiguousFailed()
    {
        using var h = new Harness(_factory);
        var row = await h.QueueAsync((await h.UserAsync()).Id);
        await h.Db.EmailOutbox.Where(o => o.Id == row.Id).ExecuteUpdateAsync(s => s
            .SetProperty(o => o.Status, EmailOutboxStatus.Sending)
            .SetProperty(o => o.ClaimedAt, Now.AddMinutes(-11)), h.Ct);

        await h.TickAsync();

        var failed = await h.ReloadAsync(row.Id);
        Assert.Equal(EmailOutboxStatus.Failed, failed.Status);
        Assert.Equal("ambiguous: claim abandoned", failed.LastError);
    }

    [Fact]
    public async Task RecentClaim_IsLeftAlone()
    {
        using var h = new Harness(_factory);
        var row = await h.QueueAsync((await h.UserAsync()).Id);
        await h.Db.EmailOutbox.Where(o => o.Id == row.Id).ExecuteUpdateAsync(s => s
            .SetProperty(o => o.Status, EmailOutboxStatus.Sending)
            .SetProperty(o => o.ClaimedAt, Now.AddMinutes(-5)), h.Ct);

        await h.TickAsync();

        Assert.Equal(EmailOutboxStatus.Sending, (await h.ReloadAsync(row.Id)).Status);
    }

    [Fact]
    public async Task Expiry_SkipsExpired()
    {
        using var h = new Harness(_factory);
        var row = EmailTestData.OutboxRow((await h.UserAsync()).Id, h.Campaign.Name, "old", Now.AddDays(-3));
        row.ExpiresAt = Now.AddSeconds(-1);
        h.Db.EmailOutbox.Add(row);
        await h.Db.SaveChangesAsync(h.Ct);

        await h.TickAsync();

        AssertSkipped(await h.ReloadAsync(row.Id), EmailSkipReasons.Expired);
        Assert.Empty(h.Transport.Sent);
    }

    [Fact]
    public async Task DailyCap_LimitsBatch()
    {
        using var h = new Harness(_factory);
        for (var i = 0; i < 2; i++)
        {
            var done = EmailTestData.OutboxRow((await h.UserAsync()).Id, h.Campaign.Name, "earlier", Now.AddHours(-2));
            done.Status = EmailOutboxStatus.Sent;
            done.SentAt = Now.AddHours(-1);
            h.Db.EmailOutbox.Add(done);
        }
        await h.Db.SaveChangesAsync(h.Ct);
        h.Options.DailyCap = 3;
        for (var i = 0; i < 5; i++)
        {
            await h.QueueAsync((await h.UserAsync()).Id);
        }

        var today = await h.TickAsync();
        Assert.Equal(1, today.Sent);

        h.Clock.SetUtcNow(Now.AddDays(1).Date.AddHours(1));
        var tomorrow = await h.TickAsync();
        Assert.Equal(3, tomorrow.Sent);
    }

    // Two dispatchers race for the same rows: B runs a whole tick after A has chosen its
    // candidates but before A claims them. Every row must be sent exactly once.
    [Fact]
    public async Task ClaimIsAtomic()
    {
        using var h = new Harness(_factory);
        var rows = new List<EmailOutbox>();
        for (var i = 0; i < 10; i++)
        {
            rows.Add(await h.QueueAsync((await h.UserAsync()).Id));
        }

        using var otherScope = _factory.Services.CreateScope();
        var otherDb = otherScope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var a = h.Dispatcher();
        var b = h.Dispatcher(otherDb);
        var raced = false;
        a.BeforeClaimForTesting = async ct =>
        {
            if (!raced)
            {
                raced = true;
                await b.RunOnceAsync(Now, ct);
            }
        };

        await a.RunOnceAsync(Now, h.Ct);

        Assert.Equal(10, h.Transport.Sent.Count);
        Assert.Equal(10, h.Transport.Sent.Select(m => m.OutboxId).Distinct().Count());
    }

    private sealed class ThrowingRenderCampaign : IEmailCampaign
    {
        public string Name => "throwing-render";
        public int PreferenceSettingTypeId => EmailSettingTypes.MonthlyRecap;
        public bool CountsTowardFrequencyCap => true;
        public bool ExemptFromFrequencyCap => false;
        public TimeSpan Lifetime => TimeSpan.FromDays(2);
        public Task<IReadOnlyList<DueEmail>> FindDueAsync(DateTimeOffset now, CancellationToken ct) => Task.FromResult<IReadOnlyList<DueEmail>>([]);
        public Task<RenderedEmail?> RenderAsync(EmailOutbox row, CancellationToken ct) => throw new InvalidOperationException("template bug");
    }
}
