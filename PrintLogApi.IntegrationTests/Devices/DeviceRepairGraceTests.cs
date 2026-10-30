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
/// <c>DeviceGrantService.RepairGrace</c>, and a retry of the same pairing request is answered
/// again instead of <c>pairing_token_used</c>. A revoke ends both at once.
/// </summary>
public class DeviceRepairGraceTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private Task<HttpResponseMessage> Send(HttpRequestMessage request) => _client.SendAsync(request, TestContext.Current.CancellationToken);

    private async Task<HttpStatusCode> MeAsync(string token)
        => (await Send(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/Users/me"), token))).StatusCode;

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
        var after = await Send(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/Users/me"), old));
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
    public async Task ARetryOfTheSamePairing_AfterALostReply_IsAnsweredAgain_AndTheOriginalTokenStaysInGrace()
    {
        var subject = NewSubject("retry");
        var deviceId = Guid.NewGuid();
        var original = TokenOf(await PairAsync(_client, subject, deviceId: deviceId));
        var pairingToken = PairingToken(subject);

        var lost = TokenOf(await PairWithAsync(pairingToken, deviceId, HttpStatusCode.OK));
        var retried = await PairWithAsync(pairingToken, deviceId, HttpStatusCode.OK);

        Assert.NotEqual(lost, TokenOf(retried));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(original));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(lost));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(TokenOf(retried)));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        Assert.Equal(1, await db.DeviceGrants.CountAsync(g => g.DeviceId == deviceId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARetryOfAFirstPairing_IsAnsweredAgain_OnTheSameGrant()
    {
        var subject = NewSubject("retry-first");
        var deviceId = Guid.NewGuid();
        var pairingToken = PairingToken(subject);

        var first = await PairWithAsync(pairingToken, deviceId, HttpStatusCode.Created);
        var retried = await PairWithAsync(pairingToken, deviceId, HttpStatusCode.OK);

        Assert.Equal(first.GetProperty("id").GetInt64(), retried.GetProperty("id").GetInt64());
        Assert.Equal(HttpStatusCode.OK, await MeAsync(TokenOf(retried)));
    }

    [Fact]
    public async Task ASpentPairingToken_StillCannotPairAnotherDevice()
    {
        var subject = NewSubject("retry-other");
        var pairingToken = PairingToken(subject);
        await PairWithAsync(pairingToken, Guid.NewGuid(), HttpStatusCode.Created);

        var other = await Send(PairRequest(pairingToken, Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal(DeviceTokenErrors.PairingTokenUsed, await CodeOf(other));
    }

    [Fact]
    public async Task ARetryAfterARevoke_NeverBringsTheDeviceBack()
    {
        var subject = NewSubject("retry-revoked");
        var deviceId = Guid.NewGuid();
        var pairingToken = PairingToken(subject);
        var paired = await PairWithAsync(pairingToken, deviceId, HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.NoContent,
            (await Send(AsUser(new HttpRequestMessage(HttpMethod.Delete, $"/api/device-grants/{paired.GetProperty("id").GetInt64()}"), subject))).StatusCode);

        var retry = await Send(PairRequest(pairingToken, deviceId));

        Assert.Equal(HttpStatusCode.Forbidden, retry.StatusCode);
        Assert.Equal(DeviceTokenErrors.PairingTokenUsed, await CodeOf(retry));

        // Nor after the user paired the device again with a new approval: the old one is bound
        // to the revoked grant, not to the device id.
        var repaired = await PairAsync(_client, subject, deviceId: deviceId);
        var replay = await Send(PairRequest(pairingToken, deviceId));
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await MeAsync(TokenOf(repaired)));
    }

    [Fact]
    public async Task ACheckIn_WithTheSupersededToken_CannotReportAKey()
    {
        var subject = NewSubject("grace-key");
        var deviceId = Guid.NewGuid();
        var old = TokenOf(await PairAsync(_client, subject, scopes: DeviceScopes.All, deviceId: deviceId));
        var current = TokenOf(await PairAsync(_client, subject, scopes: DeviceScopes.All, deviceId: deviceId));
        using var key = new Bridges.BridgeSealedCredentialTests.BridgeKey();

        // A copy of the replaced token (perhaps a thief's) must not register the bridge with
        // its key, which would be trusted without the owner's confirmation.
        var checkIn = await Send(Bridges.BridgeTestKit.CheckIn(old, new { publicKey = key.PublicKey }));
        Assert.Equal(HttpStatusCode.OK, checkIn.StatusCode);
        var body = await checkIn.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("publicKeyFingerprint").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("pendingPublicKeyFingerprint").ValueKind);

        // The current token's report counts, and since the bridge registered without a key, the
        // owner confirms it, as for any key a registered bridge reports later.
        var accepted = await Bridges.BridgeTestKit.CheckInOkAsync(_client, current, new { publicKey = key.PublicKey });
        Assert.Equal(key.Fingerprint, accepted.GetProperty("pendingPublicKeyFingerprint").GetString());
    }
}
