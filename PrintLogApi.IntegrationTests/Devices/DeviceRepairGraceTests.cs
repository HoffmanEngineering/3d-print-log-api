using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Authentication.Devices;
using Xunit;
using static PrintLogApi.IntegrationTests.Devices.DeviceTestKit;

namespace PrintLogApi.IntegrationTests.Devices;

/// <summary>
/// Re-pairing without stranding the device (api#287, item 3). A re-pair keeps the token it
/// replaces working until the new token's first use, for at most
/// <c>DeviceGrantService.RepairGrace</c>, with no more than prints and heartbeats. A pairing
/// token still pairs once. A revoke ends both tokens at once.
/// </summary>
public class DeviceRepairGraceTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private Task<HttpResponseMessage> Send(HttpRequestMessage request) => _client.SendAsync(request, TestContext.Current.CancellationToken);

    /// <summary>
    /// Whether the token works, by a heartbeat: one of the two things a superseded token may still
    /// do (api#287). OK for 200 or 201, else the status.
    /// </summary>
    private async Task<HttpStatusCode> MeAsync(string token)
    {
        var response = await Send(WithDevice(new HttpRequestMessage(HttpMethod.Put, $"/api/Connections/probe-{Guid.NewGuid():N}")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { kind = "moonraker", displayName = "probe" }),
        }, token));
        return response.StatusCode is HttpStatusCode.Created ? HttpStatusCode.OK : response.StatusCode;
    }

    private async Task<JsonElement> PairWithAsync(string pairingToken, Guid deviceId, HttpStatusCode? expected = null)
    {
        var response = await Send(PairRequest(pairingToken, deviceId));
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        if (expected is { } status)
        {
            Assert.True(response.StatusCode == status, $"expected {(int)status}, got {(int)response.StatusCode} {body}");
        }
        else
        {
            Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"pairing failed: {(int)response.StatusCode} {body}");
        }

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static string TokenOf(JsonElement paired) => paired.GetProperty("token").GetString()!;

    [Fact]
    public async Task ARePair_KeepsTheOldTokenWorking_UntilTheNewOneIsUsed()
    {
        var subject = NewSubject("grace");
        var deviceId = Guid.NewGuid();
        var old = TokenOf(await PairAsync(_client, subject, deviceId: deviceId));
        var current = TokenOf(await PairAsync(_client, subject, deviceId: deviceId));

        // The re-pair's reply may never have arrived: the device still holds the old token.
        Assert.Equal(HttpStatusCode.OK, await MeAsync(old));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(old));

        // The new token's first use proves the device has it, which ends the grace.
        Assert.Equal(HttpStatusCode.OK, await MeAsync(current));
        var after = await Send(WithDevice(new HttpRequestMessage(HttpMethod.Put, $"/api/Connections/probe-{Guid.NewGuid():N}")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { kind = "moonraker", displayName = "probe" }),
        }, old));
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.Equal(DeviceTokenErrors.InvalidToken, await CodeOf(after));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(current));
    }

    [Fact]
    public async Task ARevoke_DuringTheGrace_KillsBothTokensAtOnce()
    {
        var subject = NewSubject("grace-revoke");
        var deviceId = Guid.NewGuid();
        var old = TokenOf(await PairAsync(_client, subject, deviceId: deviceId));
        var repaired = await PairAsync(_client, subject, deviceId: deviceId);

        var revoke = await Send(AsUser(new HttpRequestMessage(HttpMethod.Delete, $"/api/device-grants/{repaired.GetProperty("id").GetInt64()}"), subject));
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(old));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(TokenOf(repaired)));
    }

    [Fact]
    public async Task RevokeAll_DuringTheGrace_KillsBothTokensAtOnce()
    {
        var subject = NewSubject("grace-revoke-all");
        var deviceId = Guid.NewGuid();
        var old = TokenOf(await PairAsync(_client, subject, deviceId: deviceId));
        var repaired = TokenOf(await PairAsync(_client, subject, deviceId: deviceId));

        Assert.Equal(HttpStatusCode.OK, (await Send(AsUser(new HttpRequestMessage(HttpMethod.Delete, "/api/device-grants"), subject))).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(old));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(repaired));
    }

    [Fact]
    public async Task ASpentPairingToken_IsRefused_ForTheSameDevice_EvenAfterTheIssuedTokenWasUsed()
    {
        var subject = NewSubject("replay");
        var deviceId = Guid.NewGuid();
        var original = TokenOf(await PairAsync(_client, subject, deviceId: deviceId));
        var pairingToken = PairingToken(subject);
        var issued = TokenOf(await PairWithAsync(pairingToken, deviceId, HttpStatusCode.OK));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(issued));

        // Whoever holds the approval and the device id must not mint another token with it:
        // that would supersede the real bridge's token.
        var replay = await Send(PairRequest(pairingToken, deviceId));

        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.Equal(DeviceTokenErrors.PairingTokenUsed, await CodeOf(replay));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(issued));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(original));
    }

    [Fact]
    public async Task ASpentPairingToken_IsRefused_RightAfterAFirstPairing()
    {
        var subject = NewSubject("replay-first");
        var deviceId = Guid.NewGuid();
        var pairingToken = PairingToken(subject);
        var first = await PairWithAsync(pairingToken, deviceId, HttpStatusCode.Created);

        var replay = await Send(PairRequest(pairingToken, deviceId));

        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.Equal(DeviceTokenErrors.PairingTokenUsed, await CodeOf(replay));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(TokenOf(first)));
    }

    [Fact]
    public async Task ASpentPairingToken_CannotPairAnotherDevice()
    {
        var subject = NewSubject("replay-other");
        var pairingToken = PairingToken(subject);
        await PairWithAsync(pairingToken, Guid.NewGuid(), HttpStatusCode.Created);

        var other = await Send(PairRequest(pairingToken, Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal(DeviceTokenErrors.PairingTokenUsed, await CodeOf(other));
    }

    [Fact]
    public async Task ASupersededToken_CannotCheckInOrReadTheDesiredList_ButStillLogs()
    {
        var subject = NewSubject("grace-scope");
        var deviceId = Guid.NewGuid();
        var old = TokenOf(await PairAsync(_client, subject, scopes: DeviceScopes.All, deviceId: deviceId));
        var current = TokenOf(await PairAsync(_client, subject, scopes: DeviceScopes.All, deviceId: deviceId));
        using var key = new Bridges.BridgeSealedCredentialTests.BridgeKey();

        // A copy of the replaced token (perhaps a thief's) must not register the bridge, report a
        // key or read sealed credentials.
        var checkIn = await Send(Bridges.BridgeTestKit.CheckIn(old, new { publicKey = key.PublicKey }));
        Assert.Equal(HttpStatusCode.Forbidden, checkIn.StatusCode);
        Assert.Equal(DeviceTokenErrors.TokenSuperseded, await CodeOf(checkIn));

        var desired = await Send(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/bridges/me/desired"), old));
        Assert.Equal(HttpStatusCode.Forbidden, desired.StatusCode);
        Assert.Equal(DeviceTokenErrors.TokenSuperseded, await CodeOf(desired));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            Assert.False(await db.Bridges.AnyAsync(b => b.Id == db.DeviceGrants.Single(g => g.DeviceId == deviceId).Id, TestContext.Current.CancellationToken));
        }

        // Prints and heartbeats keep working through the grace.
        var printerId = await CreatePrinterAsync(_client, subject);
        Assert.Equal(HttpStatusCode.Created, (await Send(PrintCreate(old, printerId))).StatusCode);

        // The current token registers the bridge, and its key is trusted as a registration's is.
        var accepted = await Bridges.BridgeTestKit.CheckInOkAsync(_client, current, new { publicKey = key.PublicKey });
        Assert.Equal(key.Fingerprint, accepted.GetProperty("publicKeyFingerprint").GetString());
    }
}
