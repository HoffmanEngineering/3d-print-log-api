using System.Text.Json.Nodes;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrintLogApi.Email;
using PrintLogApi.Email.Events;
using PrintLogApi.Email.Outbox;
using PrintLogApi.Email.Templates;
using PrintLogApi.Email.Tokens;
using PrintLogApi.IntegrationTests.Analytics;
using PrintLogApi.Models;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email;

public class SesEventProcessorTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SesEventProcessorTests(CustomWebApplicationFactory factory) => _factory = factory;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private IEmailAddressHasher Hasher => _factory.Services.GetRequiredService<IEmailAddressHasher>();

    /// <summary>An SES event as delivered through a configuration set's SNS destination.</summary>
    internal static string SesEvent(string eventType, string messageId, JsonObject detail, long? outboxId = null, string[]? destination = null)
    {
        var mail = new JsonObject
        {
            ["timestamp"] = "2026-11-02T15:00:00.000Z",
            ["messageId"] = messageId,
            ["destination"] = new JsonArray([.. (destination ?? []).Select(d => (JsonNode)d)]),
            ["tags"] = new JsonObject
            {
                ["campaign"] = new JsonArray("monthly-recap"),
                ["outbox_id"] = outboxId is { } id ? new JsonArray(id.ToString()) : new JsonArray(),
            },
        };
        var root = new JsonObject { ["eventType"] = eventType, ["mail"] = mail };
        root[eventType switch { "Bounce" => "bounce", "Complaint" => "complaint", "Delivery" => "delivery", "Reject" => "reject", _ => "other" }] = detail;
        return root.ToJsonString();
    }

    internal static string Bounce(string email, string type = "Permanent", string messageId = "msg-b")
        => SesEvent("Bounce", messageId, new JsonObject
        {
            ["bounceType"] = type,
            ["bounceSubType"] = "General",
            ["feedbackId"] = "fb-1",
            ["bouncedRecipients"] = new JsonArray(new JsonObject { ["emailAddress"] = email }),
        });

    internal static string Complaint(string? email, string messageId = "msg-c")
        => SesEvent("Complaint", messageId, new JsonObject
        {
            ["feedbackId"] = "fb-2",
            ["complainedRecipients"] = email is null ? new JsonArray() : new JsonArray(new JsonObject { ["emailAddress"] = email }),
        });

    private async Task ProcessAsync(string json)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SesEventProcessor>().ProcessAsync(json, Ct);
    }

    private async Task<bool> SuppressedAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IEmailSuppressionService>().IsSuppressedAsync(email, Ct);
    }

    private async Task<User> UserAsync(string? email = null)
    {
        using var scope = _factory.Services.CreateScope();
        return await EmailTestData.CreateUserAsync(scope.ServiceProvider.GetRequiredService<PrintLogContext>(), email: email);
    }

    private async Task<EmailPreferenceSnapshot> PrefsAsync(long userId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IEmailPreferenceService>().GetAsync(userId, Ct);
    }

    private async Task<EmailOutbox> SaveRowAsync(EmailOutbox row)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        db.EmailOutbox.Add(row);
        await db.SaveChangesAsync(Ct);
        return row;
    }

    private async Task<EmailOutbox> ReloadAsync(long id)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PrintLogContext>().EmailOutbox.AsNoTracking().SingleAsync(o => o.Id == id, Ct);
    }

    private static string Unique() => $"maker-{Guid.NewGuid():N}@example.com";

    [Fact]
    public async Task PermanentBounce_Suppresses()
    {
        var email = Unique();
        await ProcessAsync(Bounce(email));
        Assert.True(await SuppressedAsync(email));
    }

    // Matching ignores case and surrounding space, as the hash normalizes.
    [Fact]
    public async Task PermanentBounce_SuppressesRegardlessOfCase()
    {
        var email = Unique();
        await ProcessAsync(Bounce(email.ToUpperInvariant()));
        Assert.True(await SuppressedAsync(email));
    }

    [Fact]
    public async Task TransientBounce_DoesNotSuppress()
    {
        var email = Unique();
        await ProcessAsync(Bounce(email, type: "Transient"));
        Assert.False(await SuppressedAsync(email));
    }

    [Fact]
    public async Task Complaint_SuppressesAndDisablesAll()
    {
        var user = await UserAsync();
        await SaveRowAsync(Sent(user.Id, "msg-complaint-1", Hasher.Hash(user.Email!)));

        await ProcessAsync(Complaint(user.Email, messageId: "msg-complaint-1"));

        Assert.True(await SuppressedAsync(user.Email!));
        Assert.False((await PrefsAsync(user.Id)).All);
    }

    // No outbox row to go through (an old message, or one sent before outbox ids were tagged):
    // the address still finds the account.
    [Fact]
    public async Task Complaint_WithoutRow_FindsUserByAddress()
    {
        var user = await UserAsync();

        await ProcessAsync(Complaint(user.Email!.ToUpperInvariant(), messageId: $"unknown-{Guid.NewGuid():N}"));

        Assert.False((await PrefsAsync(user.Id)).All);
    }

    [Fact]
    public async Task RedactedComplaint_ResolvesThroughProviderMessageId()
    {
        var user = await UserAsync();
        var messageId = $"msg-{Guid.NewGuid():N}";
        await SaveRowAsync(Sent(user.Id, messageId, Hasher.Hash(user.Email!)));

        await ProcessAsync(Complaint(email: null, messageId: messageId));

        Assert.True(await SuppressedAsync(user.Email!));
        Assert.False((await PrefsAsync(user.Id)).All);
    }

    [Fact]
    public async Task DuplicateDelivery_OneSuppressionRow()
    {
        var email = Unique();

        await ProcessAsync(Bounce(email));
        await ProcessAsync(Bounce(email));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var hash = Hasher.Hash(email);
        Assert.Equal(1, await db.EmailSuppressions.CountAsync(s => s.EmailHash == hash, Ct));
    }

    // Review Focus 4: suppression is keyed by address, not account, so it outlives the account.
    [Fact]
    public async Task SuppressionSurvivesAccountDeletion()
    {
        var email = Unique();
        var original = await UserAsync(email);
        await ProcessAsync(Complaint(email));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var user = await db.Users.SingleAsync(u => u.Id == original.Id, Ct);
            await scope.ServiceProvider.GetRequiredService<IUserDeletionService>().DeleteAllDataForUser(user);
        }

        var returning = await UserAsync(email);
        var campaign = new TestCampaign("survives-deletion");
        var row = await SaveRowAsync(EmailTestData.OutboxRow(returning.Id, campaign.Name, "p1", DateTimeOffset.UtcNow.AddMinutes(-1)));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var options = new EmailOptions
            {
                Enabled = true,
                HoldoutPercent = 0,
                Notice = new NoticeOptions { Required = false },
                UnsubscribeSigningKeys = _factory.Services.GetRequiredService<IOptions<EmailOptions>>().Value.UnsubscribeSigningKeys,
                ApiBaseUrl = "https://api.3dprintlog.test",
                MaxSendsPerSecond = 1000,
            };
            options.Campaigns[campaign.Name] = new CampaignOptions { Enabled = true };
            var wrapped = Microsoft.Extensions.Options.Options.Create(options);
            var telemetry = new TelemetryClient(new TelemetryConfiguration());
            var clock = new SettableTimeProvider(DateTimeOffset.UtcNow);
            var transport = new FakeEmailTransport();
            var dispatcher = new EmailDispatcher(db, [campaign], new EmailPreferenceService(db, telemetry), Hasher, transport,
                new EmailTokenService(wrapped, clock), new EmailLinkBuilder(wrapped), wrapped, clock, telemetry);

            await dispatcher.RunOnceAsync(clock.GetUtcNow(), Ct);
            Assert.Empty(transport.Sent);
        }

        var skipped = await ReloadAsync(row.Id);
        Assert.Equal(EmailOutboxStatus.Skipped, skipped.Status);
        Assert.Equal(EmailSkipReasons.Suppressed, skipped.SkipReason);
    }

    [Fact]
    public async Task Delivery_ReconcilesAmbiguousFailedRow()
    {
        var user = await UserAsync();
        var row = EmailTestData.OutboxRow(user.Id, "monthly-recap", $"p-{Guid.NewGuid():N}");
        row.Status = EmailOutboxStatus.Failed;
        row.LastError = "ambiguous: claim abandoned";
        await SaveRowAsync(row);

        await ProcessAsync(SesEvent("Delivery", "msg-delivered", new JsonObject { ["timestamp"] = "2026-11-02T15:00:05.000Z" }, outboxId: row.Id));

        var reconciled = await ReloadAsync(row.Id);
        Assert.Equal(EmailOutboxStatus.Sent, reconciled.Status);
        Assert.Equal("msg-delivered", reconciled.ProviderMessageId);
        Assert.NotNull(reconciled.SentAt);
    }

    // A row that failed for a known reason did not send; a stray Delivery must not rewrite it.
    [Fact]
    public async Task Delivery_LeavesNonAmbiguousFailureAlone()
    {
        var user = await UserAsync();
        var row = EmailTestData.OutboxRow(user.Id, "monthly-recap", $"p-{Guid.NewGuid():N}");
        row.Status = EmailOutboxStatus.Failed;
        row.LastError = "rejected: bad address";
        await SaveRowAsync(row);

        await ProcessAsync(SesEvent("Delivery", "msg-x", new JsonObject(), outboxId: row.Id));

        Assert.Equal(EmailOutboxStatus.Failed, (await ReloadAsync(row.Id)).Status);
    }

    [Fact]
    public async Task Reject_RecordsLastError()
    {
        var user = await UserAsync();
        var messageId = $"msg-{Guid.NewGuid():N}";
        var row = await SaveRowAsync(Sent(user.Id, messageId, Hasher.Hash(user.Email!)));

        await ProcessAsync(SesEvent("Reject", messageId, new JsonObject { ["reason"] = "Bad content" }));

        Assert.Equal("rejected: Bad content", (await ReloadAsync(row.Id)).LastError);
    }

    [Fact]
    public async Task UnknownEventType_IsIgnored()
        => await ProcessAsync(SesEvent("Open", "msg-o", new JsonObject()));

    [Fact]
    public async Task MalformedJson_Throws()
        => await Assert.ThrowsAnyAsync<Exception>(() => ProcessAsync("{not json"));

    private static EmailOutbox Sent(long userId, string messageId, string sentTo)
    {
        var row = EmailTestData.OutboxRow(userId, "monthly-recap", $"p-{Guid.NewGuid():N}");
        row.Status = EmailOutboxStatus.Sent;
        row.SentAt = DateTimeOffset.UtcNow;
        row.ProviderMessageId = messageId;
        row.SentTo = sentTo;
        return row;
    }
}
