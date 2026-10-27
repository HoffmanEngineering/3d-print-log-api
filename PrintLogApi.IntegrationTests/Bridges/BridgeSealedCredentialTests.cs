using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static PrintLogApi.IntegrationTests.Bridges.BridgeTestKit;
using static PrintLogApi.IntegrationTests.Devices.DeviceTestKit;

namespace PrintLogApi.IntegrationTests.Bridges;

/// <summary>
/// LAN credentials sealed to the bridge's key (printlog-bridge#100, story 14a): the bridge
/// reports its public key, the owner stores ciphertext per desired printer and field, and only
/// the bridge gets it back. The API never decrypts.
/// </summary>
public class BridgeSealedCredentialTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private Task<HttpResponseMessage> Send(HttpRequestMessage request) => _client.SendAsync(request, TestContext.Current.CancellationToken);

    public sealed class BridgeKey : IDisposable
    {
        public ECDiffieHellman Key { get; } = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        public string PublicKey => Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());

        public string Fingerprint => Convert.ToHexString(SHA256.HashData(Key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

        public void Dispose() => Key.Dispose();
    }

    private sealed record Setup(PairedBridge Bridge, BridgeKey Key, long EntryId);

    /// <summary>A bridge that reported its key, with a Bambu printer connected.</summary>
    private async Task<Setup> BambuWithKey(string label)
    {
        var bridge = await PairBridgeAsync(_client, label);
        var key = new BridgeKey();
        await CheckInOkAsync(_client, bridge.Token, new { publicKey = key.PublicKey, discovered = new[] { Bambu(serial: "SN" + Guid.NewGuid().ToString("N")[..12]) } });
        var discovered = Assert.Single(await DiscoveredAsync(_client, bridge));
        var entry = await ConnectAsync(_client, bridge, discovered.GetProperty("id").GetInt64());
        return new Setup(bridge, key, entry.GetProperty("id").GetInt64());
    }

    private static string SecretUrl(Setup s, string field = "access_code") => $"/api/bridges/{s.Bridge.GrantId}/printers/{s.EntryId}/secrets/{field}";

    private static string Seal(Setup s, string plaintext, string field = "access_code", long? entryId = null)
        => CredentialSealer.Seal(s.Key.PublicKey, CredentialSealer.AssociatedData(s.Bridge.GrantId, entryId ?? s.EntryId, field), plaintext);

    private Task<HttpResponseMessage> PutSecret(Setup s, string field, string sealedValue, string? fingerprint = null)
        => Send(UserJson(HttpMethod.Put, SecretUrl(s, field), s.Bridge.Subject, new { @sealed = sealedValue, keyFingerprint = fingerprint ?? s.Key.Fingerprint }));

    private async Task<JsonElement> DesiredAsDeviceAsync(PairedBridge bridge)
    {
        var response = await Send(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/bridges/me/desired"), bridge.Token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private static JsonElement Entry(JsonElement desired, long entryId)
        => desired.GetProperty("printers").EnumerateArray().Single(p => p.GetProperty("id").GetInt64() == entryId);

    [Fact]
    public async Task ACheckIn_StoresThePublicKey_AndTheOwnerSeesItWithItsFingerprint()
    {
        var bridge = await PairBridgeAsync(_client, "key");
        using var key = new BridgeKey();

        var checkIn = await CheckInOkAsync(_client, bridge.Token, new { publicKey = key.PublicKey, publicKeyFingerprint = key.Fingerprint });

        Assert.Equal(key.Fingerprint, checkIn.GetProperty("publicKeyFingerprint").GetString());
        var dto = await GetAsUserAsync(_client, bridge.Subject, $"/api/bridges/{bridge.GrantId}");
        Assert.Equal(key.PublicKey, dto.GetProperty("publicKey").GetString());
        Assert.Equal(key.Fingerprint, dto.GetProperty("publicKeyFingerprint").GetString());
        Assert.NotEqual(JsonValueKind.Null, dto.GetProperty("publicKeyChangedAt").ValueKind);

        // Missing keeps it.
        Assert.Equal(key.Fingerprint, (await CheckInOkAsync(_client, bridge.Token, new { })).GetProperty("publicKeyFingerprint").GetString());
    }

    [Fact]
    public async Task ABridgeWithoutAKey_HasNone()
    {
        var bridge = await PairBridgeAsync(_client, "nokey");
        var checkIn = await CheckInOkAsync(_client, bridge.Token, CheckInBody());

        Assert.Equal(JsonValueKind.Null, checkIn.GetProperty("publicKeyFingerprint").ValueKind);
        var dto = await GetAsUserAsync(_client, bridge.Subject, $"/api/bridges/{bridge.GrantId}");
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("publicKey").ValueKind);
    }

    public static TheoryData<string> BadKeys()
    {
        var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Bridges", "bridge-credential-sealing.vectors.json"))).RootElement;
        return [.. vectors.GetProperty("invalidPublicKeys").EnumerateArray().Select(k => k.GetProperty("value").GetString()!)];
    }

    [Theory]
    [MemberData(nameof(BadKeys))]
    public async Task AMalformedKey_IsDroppedAndNamed_AndTheRestOfTheCheckInIsStored(string badKey)
    {
        var bridge = await PairBridgeAsync(_client, "badkey");
        using var key = new BridgeKey();
        await CheckInOkAsync(_client, bridge.Token, new { publicKey = key.PublicKey });

        var checkIn = await CheckInOkAsync(_client, bridge.Token, new { publicKey = badKey, hostName = "changed" });

        Assert.Contains(checkIn.GetProperty("rejected").EnumerateArray(),
            r => r.GetProperty("field").GetString() == "publicKey" && r.GetProperty("code").GetString() == "invalid_public_key");
        Assert.Equal(key.Fingerprint, checkIn.GetProperty("publicKeyFingerprint").GetString());
        var dto = await GetAsUserAsync(_client, bridge.Subject, $"/api/bridges/{bridge.GrantId}");
        Assert.Equal(key.PublicKey, dto.GetProperty("publicKey").GetString());
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("pendingPublicKey").ValueKind);
        Assert.Equal("changed", dto.GetProperty("hostName").GetString());
    }

    [Fact]
    public async Task AFingerprintThatDoesNotMatchTheKey_DropsTheKey()
    {
        var bridge = await PairBridgeAsync(_client, "badfp");
        using var key = new BridgeKey();
        using var other = new BridgeKey();

        var checkIn = await CheckInOkAsync(_client, bridge.Token, new { publicKey = key.PublicKey, publicKeyFingerprint = other.Fingerprint });

        Assert.Contains(checkIn.GetProperty("rejected").EnumerateArray(),
            r => r.GetProperty("field").GetString() == "publicKeyFingerprint" && r.GetProperty("code").GetString() == "invalid_public_key");
        Assert.Equal(JsonValueKind.Null, checkIn.GetProperty("publicKeyFingerprint").ValueKind);
    }

    [Fact]
    public async Task ASealedSecret_IsStoredAsCiphertext_AndOnlyTheBridgeGetsItBack_AndItOpens()
    {
        var s = await BambuWithKey("seal");
        using var _ = s.Key;
        var before = (await DesiredAsDeviceAsync(s.Bridge)).GetProperty("version").GetString();
        var sealedValue = Seal(s, "12345678");

        var response = await PutSecret(s, "access_code", sealedValue);

        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        Assert.DoesNotContain(sealedValue, text);
        var entry = JsonDocument.Parse(text).RootElement;
        Assert.Equal(["access_code"], entry.GetProperty("secretFields").EnumerateArray().Select(f => f.GetString()));
        var secret = Assert.Single(entry.GetProperty("secrets").EnumerateArray());
        Assert.Equal("access_code", secret.GetProperty("field").GetString());
        Assert.Equal(s.Key.Fingerprint, secret.GetProperty("keyFingerprint").GetString());
        Assert.False(secret.GetProperty("stale").GetBoolean());
        Assert.False(secret.TryGetProperty("sealed", out var shown) && shown.ValueKind != JsonValueKind.Null);

        // The owner's list doesn't carry it either.
        var ownerList = await Send(UserJson(HttpMethod.Get, $"/api/bridges/{s.Bridge.GrantId}/printers", s.Bridge.Subject));
        Assert.DoesNotContain(sealedValue, await ownerList.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // The bridge does, on both of its endpoints, and the desired version changed.
        var desired = await DesiredAsDeviceAsync(s.Bridge);
        Assert.NotEqual(before, desired.GetProperty("version").GetString());
        var forBridge = Assert.Single(Entry(desired, s.EntryId).GetProperty("secrets").EnumerateArray());
        Assert.Equal(sealedValue, forBridge.GetProperty("sealed").GetString());
        Assert.Equal(s.Key.Fingerprint, forBridge.GetProperty("keyFingerprint").GetString());
        var checkIn = await CheckInOkAsync(_client, s.Bridge.Token, new { });
        Assert.Equal(sealedValue, Entry(checkIn.GetProperty("desired"), s.EntryId).GetProperty("secrets")[0].GetProperty("sealed").GetString());

        // And it opens with the bridge's key and its own bridge id, entry id and field.
        var ad = CredentialSealer.AssociatedData(checkIn.GetProperty("bridgeId").GetInt64(), s.EntryId, "access_code");
        Assert.Equal("12345678", CredentialSealer.Open(s.Key.Key, forBridge.GetProperty("sealed").GetString()!, ad));

        // Only ciphertext is stored.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var row = await db.BridgePrinterSecrets.SingleAsync(x => x.BridgePrinterId == s.EntryId, TestContext.Current.CancellationToken);
        Assert.Equal(sealedValue, row.SealedValue);
    }

    [Fact]
    public async Task ASecret_CanBeReplacedAndCleared_AndEachChangeBumpsTheVersion()
    {
        var s = await BambuWithKey("replace");
        using var _ = s.Key;
        Assert.Equal(HttpStatusCode.OK, (await PutSecret(s, "access_code", Seal(s, "11111111"))).StatusCode);
        var v1 = (await DesiredAsDeviceAsync(s.Bridge)).GetProperty("version").GetString();

        var replacement = Seal(s, "22222222");
        Assert.Equal(HttpStatusCode.OK, (await PutSecret(s, "access_code", replacement)).StatusCode);
        var desired = await DesiredAsDeviceAsync(s.Bridge);
        Assert.NotEqual(v1, desired.GetProperty("version").GetString());
        Assert.Equal(replacement, Assert.Single(Entry(desired, s.EntryId).GetProperty("secrets").EnumerateArray()).GetProperty("sealed").GetString());

        var cleared = await Send(UserJson(HttpMethod.Delete, SecretUrl(s), s.Bridge.Subject));
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        var afterClear = await DesiredAsDeviceAsync(s.Bridge);
        Assert.NotEqual(desired.GetProperty("version").GetString(), afterClear.GetProperty("version").GetString());
        Assert.Empty(Entry(afterClear, s.EntryId).GetProperty("secrets").EnumerateArray());

        // Clearing what isn't there is fine and changes nothing.
        Assert.Equal(HttpStatusCode.NoContent, (await Send(UserJson(HttpMethod.Delete, SecretUrl(s), s.Bridge.Subject))).StatusCode);
        Assert.Equal(afterClear.GetProperty("version").GetString(), (await DesiredAsDeviceAsync(s.Bridge)).GetProperty("version").GetString());
    }

    [Fact]
    public async Task BadSecrets_AreRefusedWithCodes()
    {
        var s = await BambuWithKey("badsecret");
        using var _ = s.Key;

        // A field this kind doesn't take.
        var wrongField = await PutSecret(s, "password", Seal(s, "x", "password"));
        Assert.Equal(HttpStatusCode.BadRequest, wrongField.StatusCode);
        Assert.Equal("invalid_secret_field", await CodeOf(wrongField));
        Assert.Equal("invalid_secret_field", await CodeOf(await Send(UserJson(HttpMethod.Delete, SecretUrl(s, "password"), s.Bridge.Subject))));

        // Not the shape of a sealed value.
        foreach (var bad in new[] { "", "12345678", "v2." + Seal(s, "x")[3..], Seal(s, "x") + "==" })
        {
            var response = await PutSecret(s, "access_code", bad);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_sealed_value", await CodeOf(response));
        }

        var nullSealed = await Send(UserJson(HttpMethod.Put, SecretUrl(s), s.Bridge.Subject, new { keyFingerprint = s.Key.Fingerprint }));
        Assert.Equal("invalid_sealed_value", await CodeOf(nullSealed));

        // A malformed fingerprint.
        var badFingerprint = await PutSecret(s, "access_code", Seal(s, "x"), s.Key.Fingerprint.ToUpperInvariant());
        Assert.Equal(HttpStatusCode.BadRequest, badFingerprint.StatusCode);
        Assert.Equal("invalid_key_fingerprint", await CodeOf(badFingerprint));

        // Sealed to another key.
        using var other = new BridgeKey();
        var stale = await PutSecret(s, "access_code", Seal(s, "x"), other.Fingerprint);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var problem = await stale.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("bridge_key_changed", problem.GetProperty("code").GetString());
        Assert.Equal(s.Key.Fingerprint, problem.GetProperty("keyFingerprint").GetString());

        // Nothing was stored by any of it.
        Assert.Empty(Entry(await DesiredAsDeviceAsync(s.Bridge), s.EntryId).GetProperty("secrets").EnumerateArray());
    }

    [Fact]
    public async Task ABridgeThatReportedNoKey_CannotTakeSecrets()
    {
        var bridge = await PairBridgeAsync(_client, "nokeysecret");
        await CheckInOkAsync(_client, bridge.Token, CheckInBody(Bambu(serial: "SN" + Guid.NewGuid().ToString("N")[..12])));
        var entry = await ConnectAsync(_client, bridge, Assert.Single(await DiscoveredAsync(_client, bridge)).GetProperty("id").GetInt64());
        using var key = new BridgeKey();

        var response = await Send(UserJson(HttpMethod.Put, $"/api/bridges/{bridge.GrantId}/printers/{entry.GetProperty("id").GetInt64()}/secrets/access_code", bridge.Subject,
            new { @sealed = CredentialSealer.Seal(key.PublicKey, "ad", "x"), keyFingerprint = key.Fingerprint }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("bridge_key_missing", await CodeOf(response));
    }

    [Fact]
    public async Task SecretsOfABridgeOrEntryThatIsNotYours_Are404()
    {
        var s = await BambuWithKey("mine");
        using var _ = s.Key;
        var stranger = (await PairBridgeAsync(_client, "stranger")).Subject;
        var body = new { @sealed = Seal(s, "x"), keyFingerprint = s.Key.Fingerprint };

        Assert.Equal(HttpStatusCode.NotFound, (await Send(UserJson(HttpMethod.Put, SecretUrl(s), stranger, body))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(UserJson(HttpMethod.Delete, SecretUrl(s), stranger))).StatusCode);

        // An entry of another of my bridges, through this bridge's route.
        var t = await BambuWithKey("other");
        using var __ = t.Key;
        Assert.Equal(HttpStatusCode.NotFound, (await Send(UserJson(HttpMethod.Put, $"/api/bridges/{s.Bridge.GrantId}/printers/{t.EntryId}/secrets/access_code", s.Bridge.Subject, body))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(UserJson(HttpMethod.Put, $"/api/bridges/{s.Bridge.GrantId}/printers/999999999/secrets/access_code", s.Bridge.Subject, body))).StatusCode);
    }

    [Fact]
    public async Task ADeviceToken_CannotSetOrClearSecrets()
    {
        var s = await BambuWithKey("devicewrite");
        using var _ = s.Key;

        var put = await Send(WithDevice(new HttpRequestMessage(HttpMethod.Put, SecretUrl(s))
        {
            Content = JsonContent.Create(new { @sealed = Seal(s, "x"), keyFingerprint = s.Key.Fingerprint }),
        }, s.Bridge.Token));
        var delete = await Send(WithDevice(new HttpRequestMessage(HttpMethod.Delete, SecretUrl(s)), s.Bridge.Token));

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal("device_endpoint_not_allowed", await CodeOf(put));
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    private static string ConfirmUrl(Setup s) => $"/api/bridges/{s.Bridge.GrantId}/public-key/confirm";

    private Task<HttpResponseMessage> Confirm(Setup s, string fingerprint)
        => Send(UserJson(HttpMethod.Post, ConfirmUrl(s), s.Bridge.Subject, new { fingerprint }));

    [Fact]
    public async Task ANewKey_IsHeldPending_UntilTheOwnerConfirmsItsFingerprint()
    {
        var s = await BambuWithKey("rotate");
        using var _ = s.Key;
        Assert.Equal(HttpStatusCode.OK, (await PutSecret(s, "access_code", Seal(s, "11111111"))).StatusCode);
        var before = (await DesiredAsDeviceAsync(s.Bridge)).GetProperty("version").GetString();
        var userId = await UserIdAsync(factory, s.Bridge.Subject);

        using var rotated = new BridgeKey();
        var checkIn = await CheckInOkAsync(_client, s.Bridge.Token, new { publicKey = rotated.PublicKey });

        // Held: the current key stays, nothing goes stale, the version doesn't move.
        Assert.Equal(s.Key.Fingerprint, checkIn.GetProperty("publicKeyFingerprint").GetString());
        Assert.Equal(rotated.Fingerprint, checkIn.GetProperty("pendingPublicKeyFingerprint").GetString());
        Assert.Equal(before, checkIn.GetProperty("desired").GetProperty("version").GetString());
        Assert.Single(Entry(checkIn.GetProperty("desired"), s.EntryId).GetProperty("secrets").EnumerateArray());
        var dto = await GetAsUserAsync(_client, s.Bridge.Subject, $"/api/bridges/{s.Bridge.GrantId}");
        Assert.Equal(s.Key.PublicKey, dto.GetProperty("publicKey").GetString());
        Assert.Equal(rotated.PublicKey, dto.GetProperty("pendingPublicKey").GetString());
        Assert.Equal(rotated.Fingerprint, dto.GetProperty("pendingPublicKeyFingerprint").GetString());
        Assert.NotEqual(JsonValueKind.Null, dto.GetProperty("pendingPublicKeyAt").ValueKind);

        // The owner hears about it, once.
        await CheckInOkAsync(_client, s.Bridge.Token, new { publicKey = rotated.PublicKey });
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var notes = await db.Notifications.Where(n => n.UserId == userId).ToListAsync(TestContext.Current.CancellationToken);
            var note = Assert.Single(notes, n => n.Message != null && n.Message.Contains("new encryption key", StringComparison.Ordinal));
            Assert.Equal("/printers/bridges", note.ActionUrl);
        }

        // Nothing can be sealed to it yet.
        var toPending = CredentialSealer.Seal(rotated.PublicKey, CredentialSealer.AssociatedData(s.Bridge.GrantId, s.EntryId, "access_code"), "33333333");
        var refused = await PutSecret(s, "access_code", toPending, rotated.Fingerprint);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("bridge_key_unconfirmed", await CodeOf(refused));

        // Reporting the current key again leaves the pending one as it is.
        var back = await CheckInOkAsync(_client, s.Bridge.Token, new { publicKey = s.Key.PublicKey });
        Assert.Equal(rotated.Fingerprint, back.GetProperty("pendingPublicKeyFingerprint").GetString());

        // Confirming needs the pending key's own fingerprint.
        var wrong = await Confirm(s, s.Key.Fingerprint);
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Equal("pending_key_changed", await CodeOf(wrong));
        Assert.Equal("invalid_key_fingerprint", await CodeOf(await Confirm(s, "nope")));

        var confirmed = await Confirm(s, rotated.Fingerprint);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var after = await confirmed.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(rotated.PublicKey, after.GetProperty("publicKey").GetString());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("pendingPublicKey").ValueKind);

        // Now earlier values are stale: not sent, marked for the owner, and the version moved.
        var desired = await DesiredAsDeviceAsync(s.Bridge);
        Assert.NotEqual(before, desired.GetProperty("version").GetString());
        Assert.Empty(Entry(desired, s.EntryId).GetProperty("secrets").EnumerateArray());
        var owner = (await GetAsUserAsync(_client, s.Bridge.Subject, $"/api/bridges/{s.Bridge.GrantId}/printers")).EnumerateArray().Single();
        var staleSecret = Assert.Single(owner.GetProperty("secrets").EnumerateArray());
        Assert.True(staleSecret.GetProperty("stale").GetBoolean());
        Assert.Equal(s.Key.Fingerprint, staleSecret.GetProperty("keyFingerprint").GetString());
        Assert.Equal("no_pending_key", await CodeOf(await Confirm(s, rotated.Fingerprint)));

        // Sealing again to the confirmed key makes it fresh.
        Assert.Equal(HttpStatusCode.Conflict, (await PutSecret(s, "access_code", toPending, s.Key.Fingerprint)).StatusCode);
        var ok = await PutSecret(s, "access_code", toPending, rotated.Fingerprint);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var fresh = Assert.Single((await ok.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("secrets").EnumerateArray());
        Assert.False(fresh.GetProperty("stale").GetBoolean());
        var forBridge = Assert.Single(Entry(await DesiredAsDeviceAsync(s.Bridge), s.EntryId).GetProperty("secrets").EnumerateArray());
        Assert.Equal("33333333", CredentialSealer.Open(rotated.Key, forBridge.GetProperty("sealed").GetString()!, CredentialSealer.AssociatedData(s.Bridge.GrantId, s.EntryId, "access_code")));

        // Key changes are counted per bridge per day: the first key, the pending one, the confirmation.
        var counts = factory.Telemetry.Events("BridgeKeyChanged")
            .Where(e => e.Properties["GrantId"] == s.Bridge.GrantId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Select(e => (e.Properties["Change"], e.Properties["ChangesToday"]))
            .ToList();
        Assert.Equal([("first", "1"), ("pending", "2"), ("confirmed", "3")], counts);
    }

    [Fact]
    public async Task ConfirmingAKey_IsTheOwnersAlone()
    {
        var s = await BambuWithKey("confirmauth");
        using var _ = s.Key;
        using var rotated = new BridgeKey();
        await CheckInOkAsync(_client, s.Bridge.Token, new { publicKey = rotated.PublicKey });
        var stranger = (await PairBridgeAsync(_client, "stranger2")).Subject;

        var foreign = await Send(UserJson(HttpMethod.Post, ConfirmUrl(s), stranger, new { fingerprint = rotated.Fingerprint }));
        var device = await Send(WithDevice(new HttpRequestMessage(HttpMethod.Post, ConfirmUrl(s)) { Content = JsonContent.Create(new { fingerprint = rotated.Fingerprint }) }, s.Bridge.Token));

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, device.StatusCode);
        Assert.Equal("device_endpoint_not_allowed", await CodeOf(device));
        var dto = await GetAsUserAsync(_client, s.Bridge.Subject, $"/api/bridges/{s.Bridge.GrantId}");
        Assert.Equal(s.Key.PublicKey, dto.GetProperty("publicKey").GetString());
    }

    private async Task<int> KeyNotificationsAsync(PairedBridge bridge)
    {
        var userId = await UserIdAsync(factory, bridge.Subject);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.Notifications.CountAsync(n => n.UserId == userId && n.Message != null && n.Message.Contains("new encryption key"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OnlyTheRegisteringCheckIn_IsTrustedOnFirstUse_ALaterFirstKeyGoesPending()
    {
        // Registered by a bridge without a key (before 14b, or without `cryptography`): a key that
        // arrives later could come from a stolen token, so it waits for the owner like a change.
        var bridge = await PairBridgeAsync(_client, "latekey");
        await CheckInOkAsync(_client, bridge.Token, CheckInBody());
        using var key = new BridgeKey();

        var checkIn = await CheckInOkAsync(_client, bridge.Token, new { publicKey = key.PublicKey });

        Assert.Equal(JsonValueKind.Null, checkIn.GetProperty("publicKeyFingerprint").ValueKind);
        Assert.Equal(key.Fingerprint, checkIn.GetProperty("pendingPublicKeyFingerprint").GetString());
        Assert.Equal(1, await KeyNotificationsAsync(bridge));

        var confirmed = await Send(UserJson(HttpMethod.Post, $"/api/bridges/{bridge.GrantId}/public-key/confirm", bridge.Subject, new { fingerprint = key.Fingerprint }));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(key.PublicKey, (await confirmed.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("publicKey").GetString());
    }

    [Fact]
    public async Task TheOwnerIsNotified_OnlyWhenAPendingKeyAppears_AndAtMostOnceADay()
    {
        var s = await BambuWithKey("notifyonce");
        using var _ = s.Key;
        using var b = new BridgeKey();
        using var c = new BridgeKey();
        using var d = new BridgeKey();

        await CheckInOkAsync(_client, s.Bridge.Token, new { publicKey = b.PublicKey });
        await CheckInOkAsync(_client, s.Bridge.Token, new { publicKey = c.PublicKey });   // replaces a pending key: no new notice
        Assert.Equal(HttpStatusCode.OK, (await Confirm(s, c.Fingerprint)).StatusCode);
        await CheckInOkAsync(_client, s.Bridge.Token, new { publicKey = d.PublicKey });   // a new pending key, the same day

        Assert.Equal(1, await KeyNotificationsAsync(s.Bridge));
    }

    [Fact]
    public async Task AFlappingKey_StopsReplacingThePendingOne_AndIsFlagged()
    {
        var s = await BambuWithKey("flap");
        using var _ = s.Key;
        var keys = Enumerable.Range(0, 7).Select(_ => new BridgeKey()).ToList();
        try
        {
            // The first key was change 1; pending keys 2 to 5 replace each other up to the cap.
            foreach (var key in keys.Take(4))
            {
                await CheckInOkAsync(_client, s.Bridge.Token, new { publicKey = key.PublicKey });
            }

            var dto = await GetAsUserAsync(_client, s.Bridge.Subject, $"/api/bridges/{s.Bridge.GrantId}");
            Assert.Equal(keys[3].PublicKey, dto.GetProperty("pendingPublicKey").GetString());
            Assert.False(dto.GetProperty("keyChangesThrottled").GetBoolean());

            foreach (var key in keys.Skip(4))
            {
                await CheckInOkAsync(_client, s.Bridge.Token, new { publicKey = key.PublicKey });
            }

            dto = await GetAsUserAsync(_client, s.Bridge.Subject, $"/api/bridges/{s.Bridge.GrantId}");
            Assert.Equal(keys[3].PublicKey, dto.GetProperty("pendingPublicKey").GetString());
            Assert.True(dto.GetProperty("keyChangesThrottled").GetBoolean());
            Assert.Single(factory.Telemetry.Events("BridgeKeyChanged"), e =>
                e.Properties["GrantId"] == s.Bridge.GrantId.ToString(System.Globalization.CultureInfo.InvariantCulture) && e.Properties["Change"] == "throttled");

            // The kept pending key can still be confirmed.
            Assert.Equal(HttpStatusCode.OK, (await Confirm(s, keys[3].Fingerprint)).StatusCode);
        }
        finally
        {
            keys.ForEach(k => k.Dispose());
        }
    }

    [Fact]
    public async Task RePairingTheDevice_ClearsAPendingKey()
    {
        var subject = NewSubject("repair");
        var deviceId = Guid.NewGuid();
        var paired = await PairAsync(_client, subject, AllBridgeScopes, deviceId);
        var bridge = new PairedBridge(subject, paired.GetProperty("token").GetString()!, paired.GetProperty("id").GetInt64());
        using var key = new BridgeKey();
        using var other = new BridgeKey();
        await CheckInOkAsync(_client, bridge.Token, new { publicKey = key.PublicKey });
        await CheckInOkAsync(_client, bridge.Token, new { publicKey = other.PublicKey });

        var repaired = await PairAsync(_client, subject, AllBridgeScopes, deviceId);

        Assert.Equal(bridge.GrantId, repaired.GetProperty("id").GetInt64());
        var dto = await GetAsUserAsync(_client, subject, $"/api/bridges/{bridge.GrantId}");
        Assert.Equal(key.PublicKey, dto.GetProperty("publicKey").GetString());
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("pendingPublicKey").ValueKind);
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("pendingPublicKeyFingerprint").ValueKind);
    }

    [Fact]
    public async Task APendingKeyEqualToTheCurrentOne_NeverBlocksSeals_AndACheckInClearsIt()
    {
        // The state a check-in that read the bridge before a confirmation can leave behind.
        var s = await BambuWithKey("pendingcurrent");
        using var _ = s.Key;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            await db.Bridges.Where(b => b.Id == s.Bridge.GrantId).ExecuteUpdateAsync(u => u
                .SetProperty(b => b.PendingPublicKey, b => b.PublicKey)
                .SetProperty(b => b.PendingPublicKeyFingerprint, b => b.PublicKeyFingerprint), TestContext.Current.CancellationToken);
        }

        Assert.Equal(HttpStatusCode.OK, (await PutSecret(s, "access_code", Seal(s, "12345678"))).StatusCode);

        var checkIn = await CheckInOkAsync(_client, s.Bridge.Token, new { publicKey = s.Key.PublicKey });
        Assert.Equal(JsonValueKind.Null, checkIn.GetProperty("pendingPublicKeyFingerprint").ValueKind);
    }

    [Fact]
    public async Task ASecondNewKey_ReplacesThePendingOne()
    {
        var s = await BambuWithKey("twopending");
        using var _ = s.Key;
        using var first = new BridgeKey();
        using var second = new BridgeKey();
        await CheckInOkAsync(_client, s.Bridge.Token, new { publicKey = first.PublicKey });

        var checkIn = await CheckInOkAsync(_client, s.Bridge.Token, new { publicKey = second.PublicKey });

        Assert.Equal(second.Fingerprint, checkIn.GetProperty("pendingPublicKeyFingerprint").GetString());
        Assert.Equal("pending_key_changed", await CodeOf(await Confirm(s, first.Fingerprint)));
        Assert.Equal(HttpStatusCode.OK, (await Confirm(s, second.Fingerprint)).StatusCode);
    }

    [Fact]
    public async Task TheBridgeReportsWhatItCannotUse_AndTheOwnerSeesIt()
    {
        var s = await BambuWithKey("issues");
        using var _ = s.Key;
        var version = (await DesiredAsDeviceAsync(s.Bridge)).GetProperty("version").GetString();

        var checkIn = await CheckInOkAsync(_client, s.Bridge.Token, new
        {
            credentialIssuesVersion = version,
            credentialIssues = new object[]
            {
                new { entryId = s.EntryId, field = "access_code", code = "undecryptable" },
                new { entryId = 999999999L, field = "access_code", code = "missing" },
                new { entryId = s.EntryId, field = "password", code = "missing" },
                new { entryId = s.EntryId, field = "access_code", code = "on_fire" },
            },
        });

        var rejected = checkIn.GetProperty("rejected").EnumerateArray().Select(r => (r.GetProperty("field").GetString(), r.GetProperty("code").GetString())).ToList();
        Assert.Contains(("credentialIssues[1]", "unknown_printer_entry"), rejected);
        Assert.Contains(("credentialIssues[2]", "invalid_secret_field"), rejected);
        Assert.Contains(("credentialIssues[3]", "invalid_issue_code"), rejected);
        var issue = Assert.Single(Entry(checkIn.GetProperty("desired"), s.EntryId).GetProperty("credentialIssues").EnumerateArray());
        Assert.Equal("access_code", issue.GetProperty("field").GetString());
        Assert.Equal("undecryptable", issue.GetProperty("code").GetString());
        var owner = (await GetAsUserAsync(_client, s.Bridge.Subject, $"/api/bridges/{s.Bridge.GrantId}/printers")).EnumerateArray().Single();
        Assert.Equal("undecryptable", Assert.Single(owner.GetProperty("credentialIssues").EnumerateArray()).GetProperty("code").GetString());

        // Sealing a new value clears that field's issue.
        Assert.Equal(HttpStatusCode.OK, (await PutSecret(s, "access_code", Seal(s, "12345678"))).StatusCode);
        owner = (await GetAsUserAsync(_client, s.Bridge.Subject, $"/api/bridges/{s.Bridge.GrantId}/printers")).EnumerateArray().Single();
        Assert.Empty(owner.GetProperty("credentialIssues").EnumerateArray());

        // A report about an older version is ignored, so it can't land on the new value.
        await CheckInOkAsync(_client, s.Bridge.Token, new
        {
            credentialIssuesVersion = version,
            credentialIssues = new[] { new { entryId = s.EntryId, field = "access_code", code = "undecryptable" } },
        });
        owner = (await GetAsUserAsync(_client, s.Bridge.Subject, $"/api/bridges/{s.Bridge.GrantId}/printers")).EnumerateArray().Single();
        Assert.Empty(owner.GetProperty("credentialIssues").EnumerateArray());

        // The current version applies; an empty list clears.
        var current = (await DesiredAsDeviceAsync(s.Bridge)).GetProperty("version").GetString();
        await CheckInOkAsync(_client, s.Bridge.Token, new
        {
            credentialIssuesVersion = current,
            credentialIssues = new[] { new { entryId = s.EntryId, field = "access_code", code = "rejected_by_printer" } },
        });
        owner = (await GetAsUserAsync(_client, s.Bridge.Subject, $"/api/bridges/{s.Bridge.GrantId}/printers")).EnumerateArray().Single();
        Assert.Equal("rejected_by_printer", Assert.Single(owner.GetProperty("credentialIssues").EnumerateArray()).GetProperty("code").GetString());
        await CheckInOkAsync(_client, s.Bridge.Token, new
        {
            credentialIssuesVersion = current,
            credentialIssues = new[] { new { entryId = s.EntryId, field = "access_code", code = "invalid_plaintext" } },
        });
        owner = (await GetAsUserAsync(_client, s.Bridge.Subject, $"/api/bridges/{s.Bridge.GrantId}/printers")).EnumerateArray().Single();
        Assert.Equal("invalid_plaintext", Assert.Single(owner.GetProperty("credentialIssues").EnumerateArray()).GetProperty("code").GetString());
        await CheckInOkAsync(_client, s.Bridge.Token, new { credentialIssuesVersion = current, credentialIssues = Array.Empty<object>() });
        owner = (await GetAsUserAsync(_client, s.Bridge.Subject, $"/api/bridges/{s.Bridge.GrantId}/printers")).EnumerateArray().Single();
        Assert.Empty(owner.GetProperty("credentialIssues").EnumerateArray());
    }

    [Fact]
    public async Task MalformedIssueReports_RefuseTheCheckIn()
    {
        var s = await BambuWithKey("badissues");
        using var _ = s.Key;
        var version = (await DesiredAsDeviceAsync(s.Bridge)).GetProperty("version").GetString();

        var noVersion = await Send(CheckIn(s.Bridge.Token, new { credentialIssues = new[] { new { entryId = s.EntryId, field = "access_code", code = "missing" } } }));
        var tooMany = await Send(CheckIn(s.Bridge.Token, new
        {
            credentialIssuesVersion = version,
            credentialIssues = Enumerable.Range(0, 201).Select(_ => new { entryId = s.EntryId, field = "access_code", code = "missing" }).ToArray(),
        }));

        Assert.Equal("invalid_check_in", await CodeOf(noVersion));
        Assert.Equal("invalid_check_in", await CodeOf(tooMany));
    }

    [Fact]
    public async Task DisconnectingOrRevoking_DeletesTheSecrets()
    {
        var s = await BambuWithKey("delete");
        using var _ = s.Key;
        Assert.Equal(HttpStatusCode.OK, (await PutSecret(s, "access_code", Seal(s, "x"))).StatusCode);
        var t = await BambuWithKey("revoke");
        using var __ = t.Key;
        Assert.Equal(HttpStatusCode.OK, (await PutSecret(t, "access_code", Seal(t, "y"))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await Send(UserJson(HttpMethod.Delete, $"/api/bridges/{s.Bridge.GrantId}/printers/{s.EntryId}", s.Bridge.Subject))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(UserJson(HttpMethod.Delete, $"/api/device-grants/{t.Bridge.GrantId}", t.Bridge.Subject))).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        Assert.False(await db.BridgePrinterSecrets.AnyAsync(x => x.BridgePrinterId == s.EntryId || x.BridgePrinterId == t.EntryId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeletingThePrinter_DeletesItsSecrets()
    {
        var s = await BambuWithKey("printerdelete");
        using var _ = s.Key;
        Assert.Equal(HttpStatusCode.OK, (await PutSecret(s, "access_code", Seal(s, "x"))).StatusCode);
        var printerId = Entry(await DesiredAsDeviceAsync(s.Bridge), s.EntryId).GetProperty("printerId").GetInt64();

        var deleted = await Send(UserJson(HttpMethod.Delete, $"/api/Printers/{printerId}", s.Bridge.Subject));

        Assert.True(deleted.IsSuccessStatusCode, ((int)deleted.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        Assert.False(await db.BridgePrinterSecrets.AnyAsync(x => x.BridgePrinterId == s.EntryId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task APrusaLinkEntry_TakesItsThreeFields()
    {
        var bridge = await PairBridgeAsync(_client, "prusa");
        using var key = new BridgeKey();
        await CheckInOkAsync(_client, bridge.Token, new { publicKey = key.PublicKey });
        var printerId = await CreatePrinterAsync(_client, bridge.Subject);
        var connect = await Send(UserJson(HttpMethod.Post, $"/api/bridges/{bridge.GrantId}/printers", bridge.Subject, new { kind = "prusalink", host = "mk4.local", printerId }));
        var entry = await connect.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(["password", "username", "api_key"], entry.GetProperty("secretFields").EnumerateArray().Select(f => f.GetString()));
        var s = new Setup(bridge, key, entry.GetProperty("id").GetInt64());

        foreach (var field in new[] { "password", "username", "api_key" })
        {
            Assert.Equal(HttpStatusCode.OK, (await PutSecret(s, field, Seal(s, field + "-value", field))).StatusCode);
        }

        var secrets = Entry(await DesiredAsDeviceAsync(bridge), s.EntryId).GetProperty("secrets").EnumerateArray().ToList();
        Assert.Equal(3, secrets.Count);
        foreach (var secret in secrets)
        {
            var field = secret.GetProperty("field").GetString()!;
            Assert.Equal(field + "-value", CredentialSealer.Open(key.Key, secret.GetProperty("sealed").GetString()!, CredentialSealer.AssociatedData(bridge.GrantId, s.EntryId, field)));
        }
    }
}
