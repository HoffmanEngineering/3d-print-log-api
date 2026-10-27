using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Authentication.Devices;
using PrintLogApi.IntegrationTests.Mcp;
using Xunit;
using static PrintLogApi.IntegrationTests.Devices.DeviceTestKit;

namespace PrintLogApi.IntegrationTests.Devices;

/// <summary>
/// The exchange half of device pairing (#148): an Auth0 device-flow access token for the device
/// audience, from the bridge's Native app, buys one API-issued device token.
/// </summary>
public class DevicePairingTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task PairingConfig_DescribesTheAuth0DeviceFlow_Anonymously()
    {
        var response = await _client.GetAsync("/api/device-grants/pairing", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(TestJwt.DeviceClientId, body.GetProperty("clientId").GetString());
        Assert.Equal(TestJwt.DeviceAudience, body.GetProperty("audience").GetString());
        Assert.Equal("https://test.auth0.com/oauth/device/code", body.GetProperty("deviceAuthorizationEndpoint").GetString());
        Assert.Equal("https://test.auth0.com/oauth/token", body.GetProperty("tokenEndpoint").GetString());
        var scopes = body.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.Equal(DeviceScopes.All.Order(StringComparer.Ordinal), scopes);
    }

    [Fact]
    public async Task Pair_ReturnsTheTokenOnce_AndStoresOnlyItsHash()
    {
        var subject = NewSubject("hash");
        var deviceId = Guid.NewGuid();

        var paired = await PairAsync(_client, subject, deviceId: deviceId, name: "Voron 2.4 bridge");

        var token = paired.GetProperty("token").GetString()!;
        Assert.StartsWith(DeviceTokenFormat.Prefix, token, StringComparison.Ordinal);
        Assert.Equal("Bearer", paired.GetProperty("tokenType").GetString());
        Assert.Equal(deviceId, paired.GetProperty("deviceId").GetGuid());
        Assert.Equal("Voron 2.4 bridge", paired.GetProperty("name").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var row = await db.DeviceGrants.AsNoTracking().SingleAsync(g => g.DeviceId == deviceId, TestContext.Current.CancellationToken);
            Assert.NotEqual(token, row.TokenHash);
            Assert.DoesNotContain(token[DeviceTokenFormat.Prefix.Length..], row.TokenHash, StringComparison.Ordinal);
            Assert.Equal(64, row.TokenHash.Length);
            Assert.Equal(TestJwt.DeviceClientId, row.ClientId);
        }

        // Listing never hands the secret back.
        var list = await _client.SendAsync(AsUser(new HttpRequestMessage(HttpMethod.Get, "/api/device-grants"), subject), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listed = await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(token, listed, StringComparison.Ordinal);
        Assert.DoesNotContain("\"token\"", listed, StringComparison.Ordinal);
        Assert.Contains("Voron 2.4 bridge", listed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeviceToken_CreatesAPrint_RecordedAsAConnectorPrint()
    {
        var subject = NewSubject("create-print");
        var token = await TokenAsync(_client, subject);
        var printerId = await CreatePrinterAsync(_client, subject);

        var response = await _client.SendAsync(PrintCreate(token, printerId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetInt64();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var print = await db.Prints.AsNoTracking().SingleAsync(p => p.Id == id, TestContext.Current.CancellationToken);
        // The same provenance bridge prints have always had with an API key.
        Assert.Equal(PrintLogApi.Models.PrintSource.ApiKey, print.Source);
    }

    [Fact]
    public async Task Pair_RefusesATokenForAnotherAudience()
    {
        var token = TestJwt.Create(TestJwt.ApiAudience, NewSubject("aud"), BridgeScopes,
            extraClaims: [new System.Security.Claims.Claim("azp", TestJwt.DeviceClientId)]);

        var response = await _client.SendAsync(PairRequest(token, Guid.NewGuid()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("some-other-client")]
    [InlineData(null)]
    public async Task Pair_RefusesATokenFromAnotherClient(string? clientId)
    {
        var token = PairingToken(NewSubject("azp"), clientId: clientId);

        var response = await _client.SendAsync(PairRequest(token, Guid.NewGuid()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Pair_RefusesAnOldToken_WhateverItsExpiry()
    {
        var token = PairingToken(NewSubject("stale"), issuedAt: DateTime.UtcNow.AddMinutes(-30));

        var response = await _client.SendAsync(PairRequest(token, Guid.NewGuid()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Pair_RefusesATokenWithNoDeviceScope()
    {
        var token = PairingToken(NewSubject("noscope"), scopes: ["openid", "profile"]);

        var response = await _client.SendAsync(PairRequest(token, Guid.NewGuid()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("insufficient_scope", await CodeOf(response));
    }

    [Fact]
    public async Task Pair_KeepsOnlyTheDeviceScopesTheTokenCarries()
    {
        var paired = await PairAsync(_client, NewSubject("subset"), scopes: [DeviceScopes.PrintsRead, "write:printdata", "openid"]);

        var scopes = paired.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.Equal([DeviceScopes.PrintsRead], scopes);
    }

    [Fact]
    public async Task RePairing_TheSameDevice_RotatesTheTokenOnTheSameGrant()
    {
        var subject = NewSubject("repair");
        var deviceId = Guid.NewGuid();
        var first = await PairAsync(_client, subject, deviceId: deviceId);
        var second = await PairAsync(_client, subject, deviceId: deviceId, name: "renamed");

        Assert.Equal(first.GetProperty("id").GetInt64(), second.GetProperty("id").GetInt64());
        Assert.Equal("renamed", second.GetProperty("name").GetString());

        var old = await _client.SendAsync(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/Users/me"), first.GetProperty("token").GetString()!), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);

        var current = await _client.SendAsync(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/Users/me"), second.GetProperty("token").GetString()!), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        Assert.Equal(1, await db.DeviceGrants.CountAsync(g => g.DeviceId == deviceId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheSameDeviceId_UnderAnotherAccount_IsItsOwnGrant()
    {
        var deviceId = Guid.NewGuid();
        var mine = await PairAsync(_client, NewSubject("dev-a"), deviceId: deviceId);
        var theirs = await PairAsync(_client, NewSubject("dev-b"), deviceId: deviceId);

        Assert.NotEqual(mine.GetProperty("id").GetInt64(), theirs.GetProperty("id").GetInt64());

        var stillWorks = await _client.SendAsync(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/Users/me"), mine.GetProperty("token").GetString()!), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, stillWorks.StatusCode);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", "bridge")]
    [InlineData(null, "")]
    [InlineData(null, "   ")]
    [InlineData(null, null)]
    [InlineData(null, "bad\u0007name")]
    public async Task Pair_ValidatesTheDeviceIdAndName(string? deviceId, string? name)
    {
        var request = PairRequest(PairingToken(NewSubject("validate")), deviceId is null ? Guid.NewGuid() : Guid.Parse(deviceId), name);

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Pair_RefusesANameOverOneHundredCharacters()
    {
        var response = await _client.SendAsync(PairRequest(PairingToken(NewSubject("long")), Guid.NewGuid(), new string('x', 101)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Pair_IsRefused_ForAnInteractiveSession_AnApiKey_AndADeviceToken()
    {
        var body = JsonContent.Create(new { deviceId = Guid.NewGuid(), name = "x" });

        var interactive = await _client.SendAsync(
            AsUser(new HttpRequestMessage(HttpMethod.Post, "/api/device-grants") { Content = body }, IntegrationTestSeeder.TestUserOAuthId),
            TestContext.Current.CancellationToken);
        Assert.False(interactive.IsSuccessStatusCode, $"interactive got {(int)interactive.StatusCode}");

        var keyRequest = AsUser(new HttpRequestMessage(HttpMethod.Post, "/api/UserApiKeys") { Content = JsonContent.Create(new { Description = "pair test" }) }, IntegrationTestSeeder.TestUserOAuthId);
        var key = (await (await _client.SendAsync(keyRequest, TestContext.Current.CancellationToken)).Content
            .ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("publicKey").GetString()!;
        var withKey = new HttpRequestMessage(HttpMethod.Post, "/api/device-grants") { Content = JsonContent.Create(new { deviceId = Guid.NewGuid(), name = "x" }) };
        withKey.Headers.Add("X-Api-Key", key);
        var apiKey = await _client.SendAsync(withKey, TestContext.Current.CancellationToken);
        Assert.False(apiKey.IsSuccessStatusCode, $"API key got {(int)apiKey.StatusCode}");

        var device = await TokenAsync(_client, NewSubject("pair-with-device"));
        var withDevice = WithDevice(new HttpRequestMessage(HttpMethod.Post, "/api/device-grants") { Content = JsonContent.Create(new { deviceId = Guid.NewGuid(), name = "x" }) }, device);
        var deviceResponse = await _client.SendAsync(withDevice, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, deviceResponse.StatusCode);
    }

    public sealed class TwoGrantLimitFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["DevicePairing:MaxGrantsPerUser"] = "2" }));
            base.ConfigureWebHost(builder);
        }
    }

    public class Limit(TwoGrantLimitFactory limited) : IClassFixture<TwoGrantLimitFactory>
    {
        [Fact]
        public async Task Pair_BeyondTheLimit_Returns409_ButRePairingStillWorks()
        {
            var client = limited.CreateClient();
            var subject = NewSubject("limit");
            var deviceId = Guid.NewGuid();
            await PairAsync(client, subject, deviceId: deviceId);
            await PairAsync(client, subject);

            var third = await client.SendAsync(PairRequest(PairingToken(subject), Guid.NewGuid()), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
            Assert.Equal("device_grant_limit", await CodeOf(third));

            // Re-pairing a device already counted is not a new grant.
            await PairAsync(client, subject, deviceId: deviceId);
        }
    }

    public sealed class UnconfiguredFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DevicePairing:ClientId"] = "",
                    ["DevicePairing:Audience"] = "",
                }));
        }
    }

    public class Unconfigured(UnconfiguredFactory unconfigured) : IClassFixture<UnconfiguredFactory>
    {
        [Fact]
        public async Task PairingConfig_Returns503()
        {
            var response = await unconfigured.CreateClient().GetAsync("/api/device-grants/pairing", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("device_pairing_unavailable", await CodeOf(response));
        }

        [Fact]
        public async Task Pairing_FailsClosed()
        {
            var response = await unconfigured.CreateClient().SendAsync(PairRequest(PairingToken(NewSubject("unconfigured")), Guid.NewGuid()), TestContext.Current.CancellationToken);

            Assert.False(response.IsSuccessStatusCode, $"got {(int)response.StatusCode}");
        }

        [Fact]
        public async Task ExistingAuthentication_IsUnaffected()
        {
            var request = AsUser(new HttpRequestMessage(HttpMethod.Get, "/api/Users/me"), IntegrationTestSeeder.TestUserOAuthId);

            var response = await unconfigured.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}
