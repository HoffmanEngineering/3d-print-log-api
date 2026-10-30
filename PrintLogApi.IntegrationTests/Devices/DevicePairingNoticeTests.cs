using System.Net;
using System.Text.Json.Nodes;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PrintLogApi.Authentication.Devices;
using PrintLogApi.Email.Campaigns;
using PrintLogApi.IntegrationTests.Analytics;
using PrintLogApi.Models;
using PrintLogApi.Services;
using PrintLogApi.Services.DeviceGrants;
using Xunit;
using static PrintLogApi.IntegrationTests.Devices.DeviceTestKit;

namespace PrintLogApi.IntegrationTests.Devices;

/// <summary>
/// Telling the user a device paired (api#287, item 1): a security email through the outbox and
/// an in-app notification, after the grant commits, at most once per device an hour, and never
/// at the cost of the pairing.
/// </summary>
public class DevicePairingNoticeTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(List<EmailOutbox> Emails, List<Notification> Notifications)> NoticesAsync(string subject)
    {
        var userId = await Bridges.BridgeTestKit.UserIdAsync(factory, subject);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var emails = await db.EmailOutbox.AsNoTracking().Where(o => o.UserId == userId && o.Campaign == DevicePairedCampaign.CampaignName).ToListAsync(Ct);
        var notifications = await db.Notifications.AsNoTracking().Where(n => n.UserId == userId && n.Title.StartsWith("Device paired")).ToListAsync(Ct);
        return (emails, notifications);
    }

    [Fact]
    public async Task APairing_QueuesASecurityEmail_AndAnInAppNotification()
    {
        var subject = NewSubject("notice");
        var paired = await PairAsync(_client, subject, scopes: [DeviceScopes.PrintsWrite, DeviceScopes.ConnectionsWrite], name: "printlog-bridge on voron");

        var (emails, notifications) = await NoticesAsync(subject);

        var email = Assert.Single(emails);
        Assert.Equal(EmailOutboxStatus.Pending, email.Status);
        var snapshot = JsonNode.Parse(email.Exposure!)!;
        Assert.Equal(paired.GetProperty("id").GetInt64(), (long?)snapshot["grantId"]);
        Assert.Equal("printlog-bridge on voron", (string?)snapshot["deviceName"]);
        Assert.Equal("connections:write prints:write", (string?)snapshot["scopes"]);
        Assert.False((bool?)snapshot["repaired"]);

        var notification = Assert.Single(notifications);
        Assert.Contains("printlog-bridge on voron", notification.Message, StringComparison.Ordinal);
        Assert.Equal("/settings", notification.ActionUrl);
    }

    [Fact]
    public async Task ARePairWithinTheHour_IsNotNoticedAgain_ButANewDeviceAlwaysIs()
    {
        var subject = NewSubject("notice-throttle");
        var deviceId = Guid.NewGuid();
        await PairAsync(_client, subject, deviceId: deviceId);
        await PairAsync(_client, subject, deviceId: deviceId);
        await PairAsync(_client, subject, deviceId: deviceId);

        var (emails, notifications) = await NoticesAsync(subject);
        Assert.Single(emails);
        Assert.Single(notifications);

        await PairAsync(_client, subject);

        (emails, notifications) = await NoticesAsync(subject);
        Assert.Equal(2, emails.Count);
        Assert.Equal(2, notifications.Count);
    }

    [Fact]
    public async Task ARetryOfTheSamePairing_IsNotNoticedTwice()
    {
        var subject = NewSubject("notice-retry");
        var deviceId = Guid.NewGuid();
        var pairingToken = PairingToken(subject);
        Assert.Equal(HttpStatusCode.Created, (await _client.SendAsync(PairRequest(pairingToken, deviceId), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(PairRequest(pairingToken, deviceId), Ct)).StatusCode);

        var (emails, notifications) = await NoticesAsync(subject);
        Assert.Single(emails);
        Assert.Single(notifications);
    }

    [Fact]
    public async Task ARePair_AfterAnHour_IsNoticedAgain_AsARePair()
    {
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<PrintLogContext>();
        var user = await Email.EmailTestData.CreateUserAsync(db);
        var grant = new DeviceGrant
        {
            UserId = user.Id,
            DeviceId = Guid.NewGuid(),
            Name = "bridge",
            TokenHash = DeviceTokenFormat.Hash(DeviceTokenFormat.NewToken()),
            Scopes = DeviceScopes.PrintsRead,
            CreatedDate = DateTime.UtcNow,
            PairedDate = DateTime.UtcNow,
        };
        db.DeviceGrants.Add(grant);
        await db.SaveChangesAsync(Ct);
        var clock = new SettableTimeProvider(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        var notifier = new DevicePairingNotifier(db, sp.GetRequiredService<INotificationService>(), clock,
            sp.GetRequiredService<TelemetryClient>(), NullLogger<DevicePairingNotifier>.Instance);

        await notifier.NotifyAsync(grant, created: true);
        clock.SetUtcNow(clock.GetUtcNow() + DevicePairingNotifier.RepeatAfter - TimeSpan.FromMinutes(1));
        await notifier.NotifyAsync(grant, created: false);
        clock.SetUtcNow(clock.GetUtcNow() + TimeSpan.FromMinutes(1));
        await notifier.NotifyAsync(grant, created: false);

        var emails = await db.EmailOutbox.AsNoTracking().Where(o => o.UserId == user.Id).OrderBy(o => o.Id).ToListAsync(Ct);
        Assert.Equal(2, emails.Count);
        Assert.True((bool?)JsonNode.Parse(emails[1].Exposure!)!["repaired"]);
        Assert.Equal(2, await db.Notifications.CountAsync(n => n.UserId == user.Id, Ct));
    }

    /// <summary>Fails every save that adds a notification or an outbox row.</summary>
    public sealed class FailingNoticeInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => eventData.Context!.ChangeTracker.Entries().Any(e => e.State == EntityState.Added && e.Entity is Notification or EmailOutbox)
                ? throw new InvalidOperationException("notice store down")
                : ValueTask.FromResult(result);
    }

    public sealed class FailingNoticeFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.ConfigureDbContext<PrintLogContext>(options => options.AddInterceptors(new FailingNoticeInterceptor())));
        }
    }

    public class Failure(FailingNoticeFactory failing) : IClassFixture<FailingNoticeFactory>
    {
        [Fact]
        public async Task ANoticeThatCannotBeSent_NeverFailsThePairing_AndIsCounted()
        {
            var client = failing.CreateClient();
            var subject = NewSubject("notice-fail");

            var paired = await PairAsync(client, subject);

            var token = paired.GetProperty("token").GetString()!;
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/Users/me"), token), Ct)).StatusCode);
            var id = paired.GetProperty("id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var failures = failing.Telemetry.Events("DevicePairingNoticeFailed").Where(e => e.Properties["GrantId"] == id).ToList();
            Assert.Contains(failures, e => e.Properties["Channel"] == "email");
            Assert.Contains(failures, e => e.Properties["Channel"] == "in_app");
        }
    }
}
