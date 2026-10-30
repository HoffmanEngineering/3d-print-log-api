using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PrintLogApi.Authentication.Devices;
using PrintLogApi.Email.Campaigns;
using PrintLogApi.IntegrationTests.Analytics;
using PrintLogApi.Models;
using PrintLogApi.Services.DeviceGrants;
using Xunit;
using static PrintLogApi.IntegrationTests.Devices.DeviceTestKit;

namespace PrintLogApi.IntegrationTests.Devices;

/// <summary>
/// Telling the user a device paired (api#287, item 1): a security email queued in the pairing's
/// own save, so a pairing that commits always has one and a pairing whose email cannot be queued
/// fails; an in-app notification after the commit, best effort. At most once per device an hour;
/// a new device always. Both name the address the request came from.
/// </summary>
public class DevicePairingNoticeTests(DevicePairingNoticeTests.FromAddressFactory factory) : IClassFixture<DevicePairingNoticeTests.FromAddressFactory>
{
    /// <summary>The address every request in these tests comes from, as the app sees it.</summary>
    public const string ClientIp = "203.0.113.7";

    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A test sets the peer address it wants with this header; without it the peer is <see cref="ClientIp"/>.</summary>
    public const string PeerHeader = "X-Test-Peer";

    /// <summary>
    /// TestServer leaves the peer address unset; this sets it (from <see cref="PeerHeader"/>, or
    /// <see cref="ClientIp"/>), as the socket or forwarded headers would in production.
    /// </summary>
    public sealed class FromAddressFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddTransient<IStartupFilter, FromAddress>());
        }

        private sealed class FromAddress : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                app.Use((context, call) =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers[PeerHeader].FirstOrDefault() ?? ClientIp);
                    return call(context);
                });
                next(app);
            };
        }
    }

    private async Task<(List<EmailOutbox> Emails, List<Notification> Notifications)> NoticesAsync(CustomWebApplicationFactory app, string subject)
    {
        var userId = await Bridges.BridgeTestKit.UserIdAsync(app, subject);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var emails = await db.EmailOutbox.AsNoTracking().Where(o => o.UserId == userId && o.Campaign == DevicePairedCampaign.CampaignName).OrderBy(o => o.Id).ToListAsync(Ct);
        var notifications = await db.Notifications.AsNoTracking().Where(n => n.UserId == userId && n.Title.StartsWith("Device paired")).ToListAsync(Ct);
        return (emails, notifications);
    }

    [Fact]
    public async Task APairing_QueuesASecurityEmail_AndAnInAppNotification_NamingTheAddress()
    {
        var subject = NewSubject("notice");
        var deviceId = Guid.NewGuid();
        await PairAsync(_client, subject, scopes: [DeviceScopes.PrintsWrite, DeviceScopes.ConnectionsWrite], name: "printlog-bridge on voron", deviceId: deviceId);

        var (emails, notifications) = await NoticesAsync(factory, subject);

        var email = Assert.Single(emails);
        Assert.Equal(EmailOutboxStatus.Pending, email.Status);
        var snapshot = JsonNode.Parse(email.Exposure!)!;
        Assert.Equal(deviceId, snapshot["deviceId"]!.GetValue<Guid>());
        Assert.Equal("printlog-bridge on voron", (string?)snapshot["deviceName"]);
        Assert.Equal("connections:write prints:write", (string?)snapshot["scopes"]);
        Assert.Equal(ClientIp, (string?)snapshot["ipAddress"]);
        Assert.False((bool?)snapshot["repaired"]);

        var notification = Assert.Single(notifications);
        Assert.Contains("printlog-bridge on voron", notification.Message, StringComparison.Ordinal);
        Assert.Contains($"from IP address {ClientIp}", notification.Message, StringComparison.Ordinal);
        Assert.Contains("Revoke it in Settings", notification.Message, StringComparison.Ordinal);
        Assert.Equal("/settings", notification.ActionUrl);
    }

    private async Task<JsonNode> PairFromAsync(string peer, string label)
    {
        var subject = NewSubject(label);
        var request = PairRequest(PairingToken(subject), Guid.NewGuid());
        request.Headers.Add(PeerHeader, peer);
        Assert.Equal(HttpStatusCode.Created, (await _client.SendAsync(request, Ct)).StatusCode);
        var (emails, notifications) = await NoticesAsync(factory, subject);
        var snapshot = JsonNode.Parse(Assert.Single(emails).Exposure!)!;
        snapshot["message"] = Assert.Single(notifications).Message;
        return snapshot;
    }

    [Fact]
    public async Task AnIPv4MappedAddress_IsShownAsIPv4()
    {
        var snapshot = await PairFromAsync("::ffff:203.0.113.9", "notice-mapped");

        Assert.Equal("203.0.113.9", (string?)snapshot["ipAddress"]);
        Assert.Contains("from IP address 203.0.113.9.", (string?)snapshot["message"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.9")]
    [InlineData("192.168.1.20")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.130.5")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("::ffff:10.0.0.4")]
    public async Task APrivateLoopbackOrLinkLocalAddress_IsLeftOut(string peer)
    {
        // Behind App Service without forwarded headers, the peer is the front end's private
        // address: naming it would tell the owner nothing, or something wrong.
        var snapshot = await PairFromAsync(peer, "notice-private");

        Assert.Null((string?)snapshot["ipAddress"]);
        Assert.DoesNotContain("IP address", (string?)snapshot["message"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAddress_NeverReachesTelemetry()
    {
        var subject = NewSubject("notice-telemetry");
        await PairAsync(_client, subject);

        Assert.DoesNotContain(factory.Telemetry.Items.OfType<Microsoft.ApplicationInsights.DataContracts.ISupportProperties>(),
            e => e.Properties.Values.Any(v => v.Contains(ClientIp, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ARePairWithinTheHour_IsNotNoticedAgain_ButANewDeviceAlwaysIs()
    {
        var subject = NewSubject("notice-throttle");
        var deviceId = Guid.NewGuid();
        await PairAsync(_client, subject, deviceId: deviceId);
        await PairAsync(_client, subject, deviceId: deviceId);
        await PairAsync(_client, subject, deviceId: deviceId);

        var (emails, notifications) = await NoticesAsync(factory, subject);
        Assert.Single(emails);
        Assert.Single(notifications);

        await PairAsync(_client, subject);

        (emails, notifications) = await NoticesAsync(factory, subject);
        Assert.Equal(2, emails.Count);
        Assert.Equal(2, notifications.Count);
    }

    [Fact]
    public async Task AThrottledRePair_IsTracked_WithoutTheAddress()
    {
        var subject = NewSubject("notice-throttled-event");
        var deviceId = Guid.NewGuid();
        await PairAsync(_client, subject, deviceId: deviceId);
        var repaired = await PairAsync(_client, subject, deviceId: deviceId);

        var id = repaired.GetProperty("id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var throttled = Assert.Single(factory.Telemetry.Events("DevicePairingNoticeThrottled"), e => e.Properties.TryGetValue("GrantId", out var g) && g == id);
        Assert.DoesNotContain(throttled.Properties.Values, v => v.Contains(ClientIp, StringComparison.Ordinal));
    }

    private sealed class ServiceWorld(PrintLogContext db, IServiceProvider sp, long userId)
    {
        public SettableTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

        public Guid DeviceId { get; } = Guid.NewGuid();

        public async Task<bool> PairAsync(string[] scopes, string clientId = "client", string? ip = "198.51.100.4") =>
            (await new DeviceGrantService(db, sp.GetRequiredService<IMemoryCache>(), Options.Create(new DevicePairingOptions { MaxGrantsPerUser = 25 }), Clock)
                .PairAsync(userId, DeviceId, "bridge", scopes, clientId,
                    new PairingTokenSpend(DeviceTokenFormat.Hash("jti:" + Guid.NewGuid()), Clock.GetUtcNow().UtcDateTime.AddMinutes(15)), ip, Ct)).Noticed;
    }

    [Fact]
    public async Task WithinTheHour_OnlyAnIdenticalRePair_IsThrottled()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await Email.EmailTestData.CreateUserAsync(db);
        var w = new ServiceWorld(db, scope.ServiceProvider, user.Id);

        Assert.True(await w.PairAsync([DeviceScopes.PrintsWrite]));
        Assert.False(await w.PairAsync([DeviceScopes.PrintsWrite]));

        // What the owner was told no longer describes the device: tell them again.
        Assert.True(await w.PairAsync([DeviceScopes.PrintsWrite, DeviceScopes.BridgeSync]), "wider scopes");
        Assert.False(await w.PairAsync([DeviceScopes.PrintsWrite, DeviceScopes.BridgeSync]));
        Assert.True(await w.PairAsync([DeviceScopes.PrintsWrite, DeviceScopes.BridgeSync], clientId: "other-client"), "another client");
        Assert.True(await w.PairAsync([DeviceScopes.PrintsWrite, DeviceScopes.BridgeSync], clientId: "other-client", ip: "198.51.100.99"), "another address");
        Assert.False(await w.PairAsync([DeviceScopes.PrintsWrite, DeviceScopes.BridgeSync], clientId: "other-client", ip: "198.51.100.99"));

        var grant = await db.DeviceGrants.AsNoTracking().SingleAsync(g => g.DeviceId == w.DeviceId, Ct);
        Assert.DoesNotContain("198.51.100.99", System.Text.Json.JsonSerializer.Serialize(grant), StringComparison.Ordinal);
        Assert.Equal(4, await db.EmailOutbox.CountAsync(o => o.UserId == user.Id, Ct));
    }

    [Fact]
    public async Task ARePair_AfterAnHour_IsNoticedAgain_AsARePair()
    {
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<PrintLogContext>();
        var user = await Email.EmailTestData.CreateUserAsync(db);
        var clock = new SettableTimeProvider(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        var service = new DeviceGrantService(db, sp.GetRequiredService<IMemoryCache>(),
            Options.Create(new DevicePairingOptions { MaxGrantsPerUser = 25 }), clock);
        var deviceId = Guid.NewGuid();
        Task<PairedDevice> Pair() => service.PairAsync(user.Id, deviceId, "bridge", [DeviceScopes.PrintsRead], "client",
            new PairingTokenSpend(DeviceTokenFormat.Hash("jti:" + Guid.NewGuid()), clock.GetUtcNow().UtcDateTime.AddMinutes(15)), "198.51.100.4", Ct);

        Assert.True((await Pair()).Noticed);
        clock.SetUtcNow(clock.GetUtcNow() + DeviceGrantService.PairingNoticeRepeatAfter - TimeSpan.FromMinutes(1));
        Assert.False((await Pair()).Noticed);
        clock.SetUtcNow(clock.GetUtcNow() + TimeSpan.FromMinutes(1));
        Assert.True((await Pair()).Noticed);

        var emails = await db.EmailOutbox.AsNoTracking().Where(o => o.UserId == user.Id).OrderBy(o => o.Id).ToListAsync(Ct);
        Assert.Equal(2, emails.Count);
        Assert.True((bool?)JsonNode.Parse(emails[1].Exposure!)!["repaired"]);
        Assert.Equal("198.51.100.4", (string?)JsonNode.Parse(emails[1].Exposure!)!["ipAddress"]);
    }

    /// <summary>Fails every save that adds an entity of type <typeparamref name="T"/>.</summary>
    public sealed class FailingInterceptor<T> : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => eventData.Context!.ChangeTracker.Entries().Any(e => e.State == EntityState.Added && e.Entity is T)
                ? throw new InvalidOperationException("notice store down")
                : ValueTask.FromResult(result);
    }

    public class FailingFactory<T> : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.ConfigureDbContext<PrintLogContext>(options => options.AddInterceptors(new FailingInterceptor<T>())));
        }
    }

    public sealed class FailingOutboxFactory : FailingFactory<EmailOutbox>;

    public sealed class FailingNotificationFactory : FailingFactory<Notification>;

    public class OutboxFailure(FailingOutboxFactory failing) : IClassFixture<FailingOutboxFactory>
    {
        [Fact]
        public async Task APairingWhoseEmailCannotBeQueued_Fails_AndPairsNothing()
        {
            var client = failing.CreateClient();
            var subject = NewSubject("notice-outbox-down");
            var deviceId = Guid.NewGuid();
            var pairingToken = PairingToken(subject);

            // TestServer hands the unhandled exception to the client; in production it is a 500.
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(PairRequest(pairingToken, deviceId), Ct));

            using var scope = failing.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            Assert.False(await db.DeviceGrants.AnyAsync(g => g.DeviceId == deviceId, Ct));

            // Nothing committed, the spend included: the same approval fails the same way, not
            // as pairing_token_used.
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(PairRequest(pairingToken, deviceId), Ct));
        }
    }

    public class NotificationFailure(FailingNotificationFactory failing) : IClassFixture<FailingNotificationFactory>
    {
        [Fact]
        public async Task AnInAppNoticeThatCannotBeWritten_NeverFailsThePairing_AndIsCounted()
        {
            var client = failing.CreateClient();
            var subject = NewSubject("notice-in-app-down");

            var paired = await PairAsync(client, subject);

            var token = paired.GetProperty("token").GetString()!;
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/Users/me"), token), Ct)).StatusCode);
            var id = paired.GetProperty("id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var failure = Assert.Single(failing.Telemetry.Events("DevicePairingNoticeFailed"), e => e.Properties["GrantId"] == id);
            Assert.Equal("in_app", failure.Properties["Channel"]);

            using var scope = failing.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var userId = await Bridges.BridgeTestKit.UserIdAsync(failing, subject);
            Assert.True(await db.EmailOutbox.AnyAsync(o => o.UserId == userId && o.Campaign == DevicePairedCampaign.CampaignName, Ct));
        }
    }
}
