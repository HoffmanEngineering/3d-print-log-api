using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Authentication.Devices;
using PrintLogApi.Models;
using Xunit;
using static PrintLogApi.IntegrationTests.Devices.DeviceTestKit;

namespace PrintLogApi.IntegrationTests.Devices;

/// <summary>
/// The fixes from the #286 security review: pairing tokens are single-use and never
/// refresh-derived, devices may create printers under their own scope, throttling has its own
/// code, and pairing, revoking and rejected tokens are observable without token material.
/// </summary>
public class DeviceHardeningTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private Task<HttpResponseMessage> Send(HttpRequestMessage request) => _client.SendAsync(request, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Pair_RefusesATokenThatCarriesOfflineAccess()
    {
        // A refresh token can mint fresh access tokens long after the user revoked the device, so a
        // token that came with one (or from one: refreshed tokens keep the scope) cannot pair.
        var token = PairingToken(NewSubject("offline"), [.. BridgeScopes, "offline_access"]);

        var response = await Send(PairRequest(token, Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(DeviceTokenErrors.OfflineAccessNotAllowed, await CodeOf(response));
    }

    [Fact]
    public async Task APairingToken_IsSingleUse()
    {
        var subject = NewSubject("single-use");
        var token = PairingToken(subject);
        var deviceId = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Created, (await Send(PairRequest(token, deviceId))).StatusCode);

        var sameDevice = await Send(PairRequest(token, deviceId));
        Assert.Equal(HttpStatusCode.Forbidden, sameDevice.StatusCode);
        Assert.Equal(DeviceTokenErrors.PairingTokenUsed, await CodeOf(sameDevice));

        var otherDevice = await Send(PairRequest(token, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, otherDevice.StatusCode);
        Assert.Equal(DeviceTokenErrors.PairingTokenUsed, await CodeOf(otherDevice));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var userId = await db.Users.Where(u => u.OAuthUserId == subject).Select(u => u.Id).SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, await db.DeviceGrants.CountAsync(g => g.UserId == userId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ATokenWithoutJti_IsSingleUseByItsHash()
    {
        var token = PairingToken(NewSubject("no-jti"), withJti: false);

        Assert.Equal(HttpStatusCode.Created, (await Send(PairRequest(token, Guid.NewGuid()))).StatusCode);

        var again = await Send(PairRequest(token, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        Assert.Equal(DeviceTokenErrors.PairingTokenUsed, await CodeOf(again));
    }

    [Fact]
    public async Task AJtiSeenBefore_IsRefused_InAnotherToken()
    {
        var jti = Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.Created, (await Send(PairRequest(PairingToken(NewSubject("jti-a"), jti: jti), Guid.NewGuid()))).StatusCode);

        var replay = await Send(PairRequest(PairingToken(NewSubject("jti-b"), jti: jti), Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.Equal(DeviceTokenErrors.PairingTokenUsed, await CodeOf(replay));
    }

    [Fact]
    public async Task ARefusedPairing_DoesNotSpendTheToken()
    {
        var token = PairingToken(NewSubject("not-spent"));

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(PairRequest(token, Guid.NewGuid(), name: ""))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(PairRequest(token, Guid.NewGuid()))).StatusCode);
    }

    [Fact]
    public async Task ExpiredPairingTokenRecords_AreDeletedByTheNextPairing()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            db.PairingTokenUses.Add(new PairingTokenUse { TokenKeyHash = new string('e', 64), ExpiresAt = DateTime.UtcNow.AddHours(-1) });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await PairAsync(_client, NewSubject("cleanup"));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            Assert.False(await db.PairingTokenUses.AnyAsync(u => u.TokenKeyHash == new string('e', 64), TestContext.Current.CancellationToken));
            Assert.True(await db.PairingTokenUses.AnyAsync(TestContext.Current.CancellationToken));
        }
    }

    private static HttpRequestMessage CreatePrinter(string token) => WithDevice(new HttpRequestMessage(HttpMethod.Post, "/api/Printers")
    {
        Content = JsonContent.Create(new PrintLogApi.Models.DTOs.Printer.AddPrinterDTO { Name = "Voron 2.4", Make = "Voron", Model = "2.4", IsActive = true }),
    }, token);

    [Fact]
    public async Task ADevice_CreatesAPrinter_OnlyWithPrintersCreate()
    {
        var subject = NewSubject("create-printer");
        var allowed = await TokenAsync(_client, subject, [.. BridgeScopes, DeviceScopes.PrintersCreate]);
        var created = await Send(CreatePrinter(allowed));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetInt64();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var owner = await db.Users.Where(u => u.OAuthUserId == subject).Select(u => u.Id).SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(owner, (await db.Printers.SingleAsync(p => p.Id == id, TestContext.Current.CancellationToken)).UserId);
        }

        var without = await TokenAsync(_client, subject, BridgeScopes);
        var refused = await Send(CreatePrinter(without));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(DeviceTokenErrors.InsufficientScope, await CodeOf(refused));
    }

    [Fact]
    public async Task ADeviceToken401_DoesNotPointAtOAuthMetadata()
    {
        var response = await Send(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/Users/me"), "pld_" + new string('A', 43)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = response.Headers.WwwAuthenticate.ToString();
        Assert.Contains("invalid_token", challenge, StringComparison.Ordinal);
        Assert.DoesNotContain("resource_metadata", challenge, StringComparison.Ordinal);

        // Other REST 401s keep it.
        var anonymous = await Send(new HttpRequestMessage(HttpMethod.Get, "/api/Users/me"));
        Assert.Contains("resource_metadata", anonymous.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PairingAndRevoking_AreTrackedWithTheGrant_NeverTheToken()
    {
        var subject = NewSubject("telemetry");
        var paired = await PairAsync(_client, subject, scopes: [DeviceScopes.PrintsRead, DeviceScopes.ConnectionsWrite]);
        var id = paired.GetProperty("id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var token = paired.GetProperty("token").GetString()!;

        Assert.Equal(HttpStatusCode.NoContent, (await Send(AsUser(new HttpRequestMessage(HttpMethod.Delete, $"/api/device-grants/{id}"), subject))).StatusCode);

        var pairedEvent = Assert.Single(factory.Telemetry.Events("DevicePaired"), e => e.Properties.TryGetValue("GrantId", out var g) && g == id);
        Assert.Equal(PrintLogApi.IntegrationTests.Mcp.TestJwt.DeviceClientId, pairedEvent.Properties["ClientId"]);
        Assert.Equal("connections:write prints:read", pairedEvent.Properties["Scopes"]);

        var revoked = Assert.Single(factory.Telemetry.Events("DeviceGrantRevoked"), e => e.Properties.TryGetValue("GrantId", out var g) && g == id);
        Assert.Equal(PrintLogApi.IntegrationTests.Mcp.TestJwt.DeviceClientId, revoked.Properties["ClientId"]);

        Assert.DoesNotContain(factory.Telemetry.Items.OfType<Microsoft.ApplicationInsights.DataContracts.EventTelemetry>(),
            e => e.Properties.Values.Any(v => v.Contains(token[DeviceTokenFormat.Prefix.Length..], StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RejectedDeviceTokens_AreTrackedByReason()
    {
        var unknown = "pld_" + Guid.NewGuid().ToString("N") + "AAAAAAAAAAA";
        await Send(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/Users/me"), unknown));

        var token = await TokenAsync(_client, NewSubject("rejected"), [DeviceScopes.PrintsRead]);
        await Send(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/UserApiKeys"), token));

        var events = factory.Telemetry.Events("DeviceTokenRejected").ToList();
        Assert.Contains(events, e => e.Properties["Reason"] == "invalid_token");
        Assert.Contains(events, e => e.Properties["Reason"] == "endpoint_not_allowed");
        Assert.DoesNotContain(events, e => e.Properties.Values.Any(v => v.Contains("pld_", StringComparison.Ordinal)));
    }

    public sealed class OneAttemptFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Api:InvalidDeviceTokenAttemptsPerMinute"] = "1" }));
        }
    }

    public class Throttle(OneAttemptFactory throttled) : IClassFixture<OneAttemptFactory>
    {
        [Fact]
        public async Task GuessingPastTheLimit_Is429_WithItsOwnCode_NotTheRePairCode()
        {
            var client = throttled.CreateClient();
            HttpRequestMessage Guess() => WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/Users/me"), "pld_" + new string('B', 43));

            var first = await client.SendAsync(Guess(), TestContext.Current.CancellationToken);
            Assert.Equal(DeviceTokenErrors.InvalidToken, await CodeOf(first));

            var second = await client.SendAsync(Guess(), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
            Assert.Equal(DeviceTokenErrors.Throttled, await CodeOf(second));
        }
    }
}
