using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Authentication.Devices;
using Xunit;
using static PrintLogApi.IntegrationTests.Bridges.BridgeTestKit;
using static PrintLogApi.IntegrationTests.Devices.DeviceTestKit;

namespace PrintLogApi.IntegrationTests.Devices;

/// <summary>
/// <c>DELETE /api/device-grants</c> (api#287, item 2): every device of the signed-in user, at
/// once, with the same effect as revoking each one.
/// </summary>
public class DeviceRevokeAllTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private Task<HttpResponseMessage> Send(HttpRequestMessage request) => _client.SendAsync(request, TestContext.Current.CancellationToken);

    private async Task<HttpStatusCode> MeAsync(string token)
        => (await Send(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/Users/me"), token))).StatusCode;

    private Task<HttpResponseMessage> RevokeAllAsync(string subject)
        => Send(AsUser(new HttpRequestMessage(HttpMethod.Delete, "/api/device-grants"), subject));

    [Fact]
    public async Task RevokeAll_DeletesEveryGrantOfTheUser_AndReturnsTheCount()
    {
        var subject = NewSubject("revoke-all");
        var first = await TokenAsync(_client, subject);
        var second = await TokenAsync(_client, subject);
        var someoneElse = await TokenAsync(_client, NewSubject("revoke-all-other"));

        var response = await RevokeAllAsync(subject);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(2, body.GetProperty("revoked").GetInt32());

        // Effective on the next request, as a single revoke is.
        var after = await Send(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/Users/me"), first));
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.Equal(DeviceTokenErrors.InvalidToken, await CodeOf(after));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(second));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(someoneElse));

        var list = await (await Send(AsUser(new HttpRequestMessage(HttpMethod.Get, "/api/device-grants"), subject))).Content
            .ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(0, list.GetArrayLength());
    }

    [Fact]
    public async Task RevokeAll_WithNoDevices_RevokesNothing()
    {
        var response = await RevokeAllAsync(IntegrationTestSeeder.TestUserOAuthId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("revoked").GetInt32() >= 0);
        Assert.Equal(0, (await (await RevokeAllAsync(IntegrationTestSeeder.TestUserOAuthId)).Content
            .ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("revoked").GetInt32());
    }

    [Fact]
    public async Task RevokeAll_DeletesTheBridges_AndUnlinksTheirConnections()
    {
        var bridge = await PairBridgeAsync(_client, "revoke-all-bridge");
        await CheckInOkAsync(_client, bridge.Token, CheckInBody(Bambu()));
        var discovered = Assert.Single(await DiscoveredAsync(_client, bridge));
        var entry = await ConnectAsync(_client, bridge, discovered.GetProperty("id").GetInt64());
        var instanceId = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.Created, (await Send(WithDevice(new HttpRequestMessage(HttpMethod.Put, $"/api/Connections/{instanceId}")
        {
            Content = JsonContent.Create(new { kind = "bambu", displayName = "X1C", printerId = entry.GetProperty("printerId").GetInt64() }),
        }, bridge.Token))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await RevokeAllAsync(bridge.Subject)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var ct = TestContext.Current.CancellationToken;
        Assert.False(await db.Bridges.AnyAsync(b => b.Id == bridge.GrantId, ct));
        Assert.False(await db.BridgeDiscoveredPrinters.AnyAsync(d => d.BridgeId == bridge.GrantId, ct));
        Assert.False(await db.BridgePrinters.AnyAsync(p => p.BridgeId == bridge.GrantId, ct));
        var connection = await db.Connections.SingleAsync(c => c.InstanceId == instanceId, ct);
        Assert.Null(connection.BridgeId);
        Assert.Equal(entry.GetProperty("printerId").GetInt64(), connection.PrinterId);
    }

    [Fact]
    public async Task RevokeAll_IsTrackedPerGrant()
    {
        var subject = NewSubject("revoke-all-telemetry");
        var paired = await PairAsync(_client, subject);
        var id = paired.GetProperty("id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(HttpStatusCode.OK, (await RevokeAllAsync(subject)).StatusCode);

        var revoked = Assert.Single(factory.Telemetry.Events("DeviceGrantRevoked"), e => e.Properties.TryGetValue("GrantId", out var g) && g == id);
        Assert.Equal("all", revoked.Properties["Via"]);
    }

    [Fact]
    public async Task RevokeAll_RefusesADeviceToken_AndAnApiKey()
    {
        var subject = NewSubject("revoke-all-auth");
        var token = await TokenAsync(_client, subject);

        var withDevice = await Send(WithDevice(new HttpRequestMessage(HttpMethod.Delete, "/api/device-grants"), token));
        Assert.Equal(HttpStatusCode.Forbidden, withDevice.StatusCode);
        Assert.Equal(DeviceTokenErrors.EndpointNotAllowed, await CodeOf(withDevice));

        var keyRequest = AsUser(new HttpRequestMessage(HttpMethod.Post, "/api/UserApiKeys") { Content = JsonContent.Create(new { Description = "revoke all test" }) }, subject);
        var key = (await (await Send(keyRequest)).Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("publicKey").GetString()!;
        var withKey = new HttpRequestMessage(HttpMethod.Delete, "/api/device-grants");
        withKey.Headers.Add("X-Api-Key", key);
        var apiKey = await Send(withKey);
        Assert.False(apiKey.IsSuccessStatusCode, $"API key got {(int)apiKey.StatusCode}");

        Assert.Equal(HttpStatusCode.OK, await MeAsync(token));
    }
}
