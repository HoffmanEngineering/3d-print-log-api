using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static PrintLogApi.IntegrationTests.Bridges.BridgeTestKit;
using static PrintLogApi.IntegrationTests.Devices.DeviceTestKit;

namespace PrintLogApi.IntegrationTests.Bridges;

/// <summary>
/// Moving a printer between two of the user's bridges (printlog-bridge#103, story 15a): the entry
/// leaves the old bridge's desired list and joins the new one's in one save, keeping its id. Its
/// sealed credentials can't follow (they are sealed to the old bridge's key and bound to its id),
/// so they are dropped and named for the owner to enter again.
/// </summary>
public class BridgeMoveTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private Task<HttpResponseMessage> Send(HttpRequestMessage request) => _client.SendAsync(request, TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> Move(PairedBridge from, long entryId, object body)
        => Send(UserJson(HttpMethod.Post, $"/api/bridges/{from.GrantId}/printers/{entryId}/move", from.Subject, body));

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<(int Status, string? ETag, JsonElement? Body)> Desired(PairedBridge bridge, string? etag = null)
    {
        var request = WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/bridges/me/desired"), bridge.Token);
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        var response = await Send(request);
        return ((int)response.StatusCode, response.Headers.ETag?.ToString(),
            response.StatusCode == HttpStatusCode.OK ? await Json(response) : null);
    }

    private static long? BridgeIdIn(JsonElement problem)
        => problem.TryGetProperty("bridgeId", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : null;

    /// <summary>Two bridges of one user on two networks; the workshop one has a key.</summary>
    private async Task<(PairedBridge A, PairedBridge B)> TwoBridgesAsync(string label, object? bDiscovered = null, string? aKey = null, string? bKey = null)
    {
        var a = await PairBridgeAsync(_client, label);
        var paired = await PairAsync(_client, a.Subject, AllBridgeScopes, name: "printlog-bridge on workshop");
        var b = new PairedBridge(a.Subject, paired.GetProperty("token").GetString()!, paired.GetProperty("id").GetInt64());
        // A key is taken as is only on the check-in that registers the bridge.
        await CheckInOkAsync(_client, a.Token, new { publicKey = aKey, networks = new[] { "192.168.1.0/24" }, discovered = Array.Empty<object>() });
        await CheckInOkAsync(_client, b.Token, new
        {
            publicKey = bKey,
            networks = new[] { "192.168.2.0/24" },
            discovered = bDiscovered is null ? Array.Empty<object>() : new[] { bDiscovered },
        });
        return (a, b);
    }

    private async Task<long> ConnectByHandAsync(PairedBridge bridge, string host, string kind = "moonraker", string? serial = null)
    {
        var response = await Send(UserJson(HttpMethod.Post, $"/api/bridges/{bridge.GrantId}/printers", bridge.Subject,
            new { kind, host, serial, printerId = await CreatePrinterAsync(_client, bridge.Subject) }));
        var body = await Json(response);
        Assert.True(response.StatusCode == HttpStatusCode.Created, body.ToString());
        return body.GetProperty("id").GetInt64();
    }

    private static string Serial() => "MV" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    [Fact]
    public async Task AMove_RebindsTheEntry_ToTheOtherBridge_AndBothDesiredListsChange()
    {
        var serial = Serial();
        var (a, b) = await TwoBridgesAsync("move", Bambu(serial, address: "192.168.2.61"));
        await CheckInOkAsync(_client, a.Token, CheckInBody(Bambu(serial)));
        var entry = await ConnectAsync(_client, a, Assert.Single(await DiscoveredAsync(_client, a)).GetProperty("id").GetInt64());
        var entryId = entry.GetProperty("id").GetInt64();
        var aBefore = await Desired(a);
        var bBefore = await Desired(b);
        Assert.Single(aBefore.Body!.Value.GetProperty("printers").EnumerateArray());
        Assert.Empty(bBefore.Body!.Value.GetProperty("printers").EnumerateArray());

        var foundOnB = Assert.Single(await DiscoveredAsync(_client, b)).GetProperty("id").GetInt64();
        var response = await Move(a, entryId, new { toBridgeId = b.GrantId, discoveredPrinterId = foundOnB });

        var moved = await Json(response);
        Assert.True(response.StatusCode == HttpStatusCode.OK, moved.ToString());
        Assert.Equal(a.GrantId, moved.GetProperty("fromBridgeId").GetInt64());
        Assert.Equal(b.GrantId, moved.GetProperty("toBridgeId").GetInt64());
        var printer = moved.GetProperty("printer");
        Assert.Equal(entryId, printer.GetProperty("id").GetInt64());
        Assert.Equal(entry.GetProperty("printerId").GetInt64(), printer.GetProperty("printerId").GetInt64());
        Assert.Equal("192.168.2.61", printer.GetProperty("host").GetString());

        // The old bridge's list no longer has it, the new one's does, and both versions moved.
        var aAfter = await Desired(a, aBefore.ETag);
        var bAfter = await Desired(b, bBefore.ETag);
        Assert.Equal(200, aAfter.Status);
        Assert.Equal(200, bAfter.Status);
        Assert.Empty(aAfter.Body!.Value.GetProperty("printers").EnumerateArray());
        Assert.Equal(entryId, Assert.Single(bAfter.Body!.Value.GetProperty("printers").EnumerateArray()).GetProperty("id").GetInt64());

        Assert.Empty((await GetAsUserAsync(_client, a.Subject, $"/api/bridges/{a.GrantId}/printers")).EnumerateArray());
        Assert.Equal((int)Models.DTOs.Bridges.DiscoveredPrinterState.Connected,
            Assert.Single(await DiscoveredAsync(_client, b)).GetProperty("state").GetInt32());
        Assert.Equal((int)Models.DTOs.Bridges.DiscoveredPrinterState.New,
            Assert.Single(await DiscoveredAsync(_client, a)).GetProperty("state").GetInt32());
    }

    [Fact]
    public async Task AMove_WithoutADiscovery_KeepsTheAddress_OrTakesTheOneGiven()
    {
        var (a, b) = await TwoBridgesAsync("move-hand");
        var kept = await ConnectByHandAsync(a, "192.168.1.5");
        var readdressed = await ConnectByHandAsync(a, "192.168.1.6");

        var keptMove = await Json(await Move(a, kept, new { toBridgeId = b.GrantId }));
        Assert.Equal("192.168.1.5", keptMove.GetProperty("printer").GetProperty("host").GetString());
        Assert.Equal("moonraker:ip:192.168.1.5:7125", keptMove.GetProperty("printer").GetProperty("identityKey").GetString());

        var readdressedMove = await Json(await Move(a, readdressed, new { toBridgeId = b.GrantId, host = "192.168.2.6", port = 7126 }));
        var printer = readdressedMove.GetProperty("printer");
        Assert.Equal("192.168.2.6", printer.GetProperty("host").GetString());
        Assert.Equal(7126, printer.GetProperty("port").GetInt32());
        Assert.Equal("moonraker:ip:192.168.2.6:7126", printer.GetProperty("identityKey").GetString());
        Assert.Equal(2, (await GetAsUserAsync(_client, b.Subject, $"/api/bridges/{b.GrantId}/printers")).GetArrayLength());
    }

    private sealed record Credentialed(PairedBridge A, PairedBridge B, BridgeSealedCredentialTests.BridgeKey AKey, BridgeSealedCredentialTests.BridgeKey BKey, long EntryId) : IDisposable
    {
        public void Dispose()
        {
            AKey.Dispose();
            BKey.Dispose();
        }

        /// <summary>A value sealed as the web app would for the new bridge: its key, its id.</summary>
        public string SealForB(string field, string plaintext = "12345678")
            => CredentialSealer.Seal(BKey.PublicKey, CredentialSealer.AssociatedData(B.GrantId, EntryId, field), plaintext);
    }

    /// <summary>Two bridges with keys, and a printer of <paramref name="kind"/> on the first with these credentials sealed.</summary>
    private async Task<Credentialed> CredentialedAsync(string label, string kind, params string[] fields)
    {
        var aKey = new BridgeSealedCredentialTests.BridgeKey();
        var bKey = new BridgeSealedCredentialTests.BridgeKey();
        var (a, b) = await TwoBridgesAsync(label, aKey: aKey.PublicKey, bKey: bKey.PublicKey);
        var entryId = await ConnectByHandAsync(a, "192.168.1.60", kind, kind == "bambu" ? Serial() : null);
        foreach (var field in fields)
        {
            var sealedValue = CredentialSealer.Seal(aKey.PublicKey, CredentialSealer.AssociatedData(a.GrantId, entryId, field), "secret-" + field);
            Assert.Equal(HttpStatusCode.OK, (await Send(UserJson(HttpMethod.Put, $"/api/bridges/{a.GrantId}/printers/{entryId}/secrets/{field}", a.Subject,
                new { @sealed = sealedValue, keyFingerprint = aKey.Fingerprint }))).StatusCode);
        }

        return new Credentialed(a, b, aKey, bKey, entryId);
    }

    /// <summary>
    /// Without its access code the new bridge can't watch the printer, and the old one stops within
    /// a minute: the move would leave it unwatched. So the credentials come with the move.
    /// </summary>
    [Fact]
    public async Task ACredentialedPrinter_CannotMoveWithoutItsCredentials()
    {
        using var c = await CredentialedAsync("move-needs-secrets", "bambu", "access_code");

        var response = await Move(c.A, c.EntryId, new { toBridgeId = c.B.GrantId, host = "192.168.2.60" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await Json(response);
        Assert.Equal("secrets_required", problem.GetProperty("code").GetString());
        Assert.Equal(["access_code"], problem.GetProperty("fields").EnumerateArray().Select(f => f.GetString()).ToArray());
        Assert.Single((await GetAsUserAsync(_client, c.A.Subject, $"/api/bridges/{c.A.GrantId}/printers")).EnumerateArray());
    }

    [Fact]
    public async Task ACredentialedPrinter_MovesWithItsCredentialsSealedToTheNewBridge_InOneStep()
    {
        using var c = await CredentialedAsync("move-with-secrets", "bambu", "access_code");
        var version = (await CheckInOkAsync(_client, c.A.Token, new { })).GetProperty("desired").GetProperty("version").GetString();
        await CheckInOkAsync(_client, c.A.Token, new
        {
            credentialIssuesVersion = version,
            credentialIssues = new[] { new { entryId = c.EntryId, field = "access_code", code = "rejected_by_printer" } },
        });
        var sealedForB = c.SealForB("access_code");

        var response = await Move(c.A, c.EntryId, new
        {
            toBridgeId = c.B.GrantId,
            host = "192.168.2.60",
            keyFingerprint = c.BKey.Fingerprint,
            secrets = new Dictionary<string, string> { ["access_code"] = sealedForB },
        });

        var moved = await Json(response);
        Assert.True(response.StatusCode == HttpStatusCode.OK, moved.ToString());
        Assert.Empty(moved.GetProperty("droppedSecretFields").EnumerateArray());
        var secret = Assert.Single(moved.GetProperty("printer").GetProperty("secrets").EnumerateArray());
        Assert.Equal(c.BKey.Fingerprint, secret.GetProperty("keyFingerprint").GetString());
        Assert.False(secret.GetProperty("stale").GetBoolean());
        Assert.Empty(moved.GetProperty("printer").GetProperty("credentialIssues").EnumerateArray());

        // The new bridge gets it with its first poll; the old bridge's value is gone.
        var onB = Assert.Single(Assert.Single((await Desired(c.B)).Body!.Value.GetProperty("printers").EnumerateArray()).GetProperty("secrets").EnumerateArray());
        Assert.Equal(sealedForB, onB.GetProperty("sealed").GetString());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        Assert.Equal(c.BKey.Fingerprint, Assert.Single(await db.BridgePrinterSecrets.Where(s => s.BridgePrinterId == c.EntryId).ToListAsync(TestContext.Current.CancellationToken)).KeyFingerprint);
    }

    /// <summary>The values are checked as the seal endpoint checks them, against the new bridge's key.</summary>
    [Fact]
    public async Task TheCredentialsOfAMove_AreCheckedLikeASeal()
    {
        using var c = await CredentialedAsync("move-secret-checks", "bambu", "access_code");

        async Task Refused(object body, HttpStatusCode status, string code)
        {
            var response = await Move(c.A, c.EntryId, body);
            Assert.Equal(status, response.StatusCode);
            Assert.Equal(code, await CodeOf(response));
        }

        var good = c.SealForB("access_code");
        await Refused(new { toBridgeId = c.B.GrantId, keyFingerprint = c.AKey.Fingerprint, secrets = new Dictionary<string, string> { ["access_code"] = good } },
            HttpStatusCode.Conflict, "bridge_key_changed");
        await Refused(new { toBridgeId = c.B.GrantId, keyFingerprint = c.BKey.Fingerprint, secrets = new Dictionary<string, string> { ["access_code"] = "v1.nope" } },
            HttpStatusCode.BadRequest, "invalid_sealed_value");
        await Refused(new { toBridgeId = c.B.GrantId, keyFingerprint = c.BKey.Fingerprint, secrets = new Dictionary<string, string> { ["access_code"] = good, ["password"] = c.SealForB("password") } },
            HttpStatusCode.BadRequest, "invalid_secret_field");
        await Refused(new { toBridgeId = c.B.GrantId, keyFingerprint = "zz", secrets = new Dictionary<string, string> { ["access_code"] = good } },
            HttpStatusCode.BadRequest, "invalid_key_fingerprint");

        // A bridge with no key can't take the move's credentials.
        var keyless = await PairAsync(_client, c.A.Subject, AllBridgeScopes, name: "printlog-bridge, old");
        var keylessBridge = new PairedBridge(c.A.Subject, keyless.GetProperty("token").GetString()!, keyless.GetProperty("id").GetInt64());
        await CheckInOkAsync(_client, keylessBridge.Token, new { networks = new[] { "192.168.3.0/24" } });
        await Refused(new { toBridgeId = keylessBridge.GrantId, keyFingerprint = c.BKey.Fingerprint, secrets = new Dictionary<string, string> { ["access_code"] = good } },
            HttpStatusCode.Conflict, "bridge_key_missing");

        Assert.Single((await GetAsUserAsync(_client, c.A.Subject, $"/api/bridges/{c.A.GrantId}/printers")).EnumerateArray());
    }

    /// <summary>
    /// PrusaLink takes a password or an API key, and a user name only beside the password. One of
    /// the two carries the move; what isn't sent is dropped and named.
    /// </summary>
    [Fact]
    public async Task APrusaLinkPrinter_MovesWithEitherOfItsCredentials_AndNamesWhatWasDropped()
    {
        using var c = await CredentialedAsync("move-prusalink", "prusalink", "password", "username");

        var needs = await Move(c.A, c.EntryId, new { toBridgeId = c.B.GrantId });
        Assert.Equal(HttpStatusCode.BadRequest, needs.StatusCode);
        Assert.Equal(["password"], (await Json(needs)).GetProperty("fields").EnumerateArray().Select(f => f.GetString()).ToArray());

        var moved = await Json(await Move(c.A, c.EntryId, new
        {
            toBridgeId = c.B.GrantId,
            keyFingerprint = c.BKey.Fingerprint,
            secrets = new Dictionary<string, string> { ["api_key"] = c.SealForB("api_key") },
        }));

        Assert.Equal(["password", "username"], moved.GetProperty("droppedSecretFields").EnumerateArray().Select(f => f.GetString()).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("api_key", Assert.Single(moved.GetProperty("printer").GetProperty("secrets").EnumerateArray()).GetProperty("field").GetString());
    }

    /// <summary>A Moonraker API key is set only for a Moonraker with [authorization], which can't be watched without it.</summary>
    [Fact]
    public async Task AMoonrakerApiKeyThatWasSet_IsRequiredForTheMove()
    {
        using var c = await CredentialedAsync("move-moonraker-key", "moonraker", "moonraker_api_key");

        var response = await Move(c.A, c.EntryId, new { toBridgeId = c.B.GrantId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("secrets_required", await CodeOf(response));
    }

    [Fact]
    public async Task AMove_WithNothingToDrop_ListsNoFields()
    {
        var (a, b) = await TwoBridgesAsync("move-nothing");
        var entryId = await ConnectByHandAsync(a, "192.168.1.7");

        var moved = await Json(await Move(a, entryId, new { toBridgeId = b.GrantId }));

        Assert.Empty(moved.GetProperty("droppedSecretFields").EnumerateArray());
    }

    [Fact]
    public async Task AMove_ToAnUnusableBridge_IsRefused()
    {
        var (a, _) = await TwoBridgesAsync("move-target");
        var entryId = await ConnectByHandAsync(a, "192.168.1.8");
        var stranger = await PairBridgeAsync(_client, "move-stranger");
        await CheckInOkAsync(_client, stranger.Token, CheckInBody());

        foreach (var target in new long?[] { null, a.GrantId, stranger.GrantId, 987654321 })
        {
            var response = await Move(a, entryId, new { toBridgeId = target });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_target_bridge", await CodeOf(response));
        }

        Assert.Single((await GetAsUserAsync(_client, a.Subject, $"/api/bridges/{a.GrantId}/printers")).EnumerateArray());
    }

    [Fact]
    public async Task AMove_OfSomethingNotYours_IsNotFound()
    {
        var (a, b) = await TwoBridgesAsync("move-404");
        var entryId = await ConnectByHandAsync(a, "192.168.1.9");
        var stranger = await PairBridgeAsync(_client, "move-404-stranger");
        await CheckInOkAsync(_client, stranger.Token, CheckInBody());

        Assert.Equal(HttpStatusCode.NotFound, (await Move(a, entryId + 100000, new { toBridgeId = b.GrantId })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(UserJson(HttpMethod.Post, $"/api/bridges/{a.GrantId}/printers/{entryId}/move", stranger.Subject,
            new { toBridgeId = stranger.GrantId }))).StatusCode);
    }

    /// <summary>An entry that already left this bridge names where it went, so a stale screen can follow.</summary>
    [Fact]
    public async Task AMove_FromABridgeTheEntryAlreadyLeft_SaysWhereItIs()
    {
        var (a, b) = await TwoBridgesAsync("move-twice");
        var entryId = await ConnectByHandAsync(a, "192.168.1.10");
        Assert.Equal(HttpStatusCode.OK, (await Move(a, entryId, new { toBridgeId = b.GrantId })).StatusCode);

        var again = await Move(a, entryId, new { toBridgeId = b.GrantId });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var problem = await Json(again);
        Assert.Equal("printer_moved", problem.GetProperty("code").GetString());
        Assert.Equal(b.GrantId, BridgeIdIn(problem));
    }

    [Fact]
    public async Task AMove_IsRefused_WhenTheNewBridgeHasThatPrinterInItsConfigFile()
    {
        // The new bridge's config file has the address it would reach the printer at.
        var configured = new Dictionary<string, object?>(JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(Moonraker(mac: "b8:27:eb:00:00:61", address: "192.168.2.61")))!) { ["configured"] = true };
        var (a, b) = await TwoBridgesAsync("move-configured-target", configured);
        var entryId = await ConnectByHandAsync(a, "192.168.1.61");

        var response = await Move(a, entryId, new { toBridgeId = b.GrantId, host = "192.168.2.61" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await Json(response);
        Assert.Equal("printer_configured_on_bridge", problem.GetProperty("code").GetString());
        Assert.Equal(b.GrantId, BridgeIdIn(problem));
        Assert.Single((await GetAsUserAsync(_client, a.Subject, $"/api/bridges/{a.GrantId}/printers")).EnumerateArray());
    }

    /// <summary>
    /// The old bridge's config file wins on that bridge: moving the app entry away would leave it
    /// watched there and on the new bridge.
    /// </summary>
    [Fact]
    public async Task AMove_IsRefused_WhenTheOldBridgeHasThatPrinterInItsConfigFile()
    {
        var (a, b) = await TwoBridgesAsync("move-configured-source");
        var entryId = await ConnectByHandAsync(a, "192.168.1.50");
        var configured = new Dictionary<string, object?>(JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(Moonraker(address: "192.168.1.50")))!) { ["configured"] = true };
        await CheckInOkAsync(_client, a.Token, CheckInBody(configured));

        var response = await Move(a, entryId, new { toBridgeId = b.GrantId, host = "192.168.2.50" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await Json(response);
        Assert.Equal("printer_configured_on_bridge", problem.GetProperty("code").GetString());
        Assert.Equal(a.GrantId, BridgeIdIn(problem));
    }

    [Fact]
    public async Task AMove_OntoAnAddressTheNewBridgeAlreadyWatches_IsRefused()
    {
        var (a, b) = await TwoBridgesAsync("move-collide");
        var entryId = await ConnectByHandAsync(a, "192.168.1.11");
        await ConnectByHandAsync(b, "192.168.2.11");

        var response = await Move(a, entryId, new { toBridgeId = b.GrantId, host = "192.168.2.11" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("discovered_printer_already_connected", await CodeOf(response));
    }

    [Fact]
    public async Task AMove_ToADiscoveredPrinterOfAnotherKindOrBridge_IsRefused()
    {
        var (a, b) = await TwoBridgesAsync("move-kind", Bambu(Serial(), address: "192.168.2.62"));
        var entryId = await ConnectByHandAsync(a, "192.168.1.12");
        await CheckInOkAsync(_client, a.Token, CheckInBody(Moonraker(mac: "b8:27:eb:00:00:12", address: "192.168.1.99")));

        var otherKind = await Move(a, entryId, new { toBridgeId = b.GrantId, discoveredPrinterId = Assert.Single(await DiscoveredAsync(_client, b)).GetProperty("id").GetInt64() });
        Assert.Equal(HttpStatusCode.BadRequest, otherKind.StatusCode);
        Assert.Equal("invalid_kind", await CodeOf(otherKind));

        var notOnTarget = await Move(a, entryId, new { toBridgeId = b.GrantId, discoveredPrinterId = Assert.Single(await DiscoveredAsync(_client, a)).GetProperty("id").GetInt64() });
        Assert.Equal(HttpStatusCode.BadRequest, notOnTarget.StatusCode);
        Assert.Equal("discovered_printer_not_found", await CodeOf(notOnTarget));

        var badHost = await Move(a, entryId, new { toBridgeId = b.GrantId, host = "8.8.8.8" });
        Assert.Equal(HttpStatusCode.BadRequest, badHost.StatusCode);
        Assert.Equal("invalid_host", await CodeOf(badHost));
    }

    [Fact]
    public async Task ADeviceToken_CannotMoveAPrinter()
    {
        var (a, b) = await TwoBridgesAsync("move-device");
        var entryId = await ConnectByHandAsync(a, "192.168.1.13");

        var response = await Send(WithDevice(new HttpRequestMessage(HttpMethod.Post, $"/api/bridges/{a.GrantId}/printers/{entryId}/move")
        {
            Content = JsonContent.Create(new { toBridgeId = b.GrantId }),
        }, a.Token));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("device_endpoint_not_allowed", await CodeOf(response));
    }

    /// <summary>Prints the old bridge logged stay linked to its connection.</summary>
    [Fact]
    public async Task PrintsLoggedBeforeAMove_KeepTheirConnection()
    {
        var (a, b) = await TwoBridgesAsync("move-history");
        var entryId = await ConnectByHandAsync(a, "192.168.1.14");
        var printerId = (await GetAsUserAsync(_client, a.Subject, $"/api/bridges/{a.GrantId}/printers")).EnumerateArray().Single().GetProperty("printerId").GetInt64();
        var instanceId = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.Created, (await Send(WithDevice(new HttpRequestMessage(HttpMethod.Put, $"/api/Connections/{instanceId}")
        {
            Content = JsonContent.Create(new { kind = "moonraker", displayName = "voron", printerId }),
        }, a.Token))).StatusCode);
        var created = await Send(WithDevice(new HttpRequestMessage(HttpMethod.Post, "/api/Prints")
        {
            Content = JsonContent.Create(new { title = "before", printerId, connectionInstanceId = instanceId }),
        }, a.Token));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var printId = (await Json(created)).GetProperty("id").GetInt64();

        Assert.Equal(HttpStatusCode.OK, (await Move(a, entryId, new { toBridgeId = b.GrantId })).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var ct = TestContext.Current.CancellationToken;
        var connectionId = await db.Prints.Where(p => p.Id == printId).Select(p => p.ConnectionId).SingleAsync(ct);
        Assert.Equal(a.GrantId, await db.Connections.Where(c => c.Id == connectionId).Select(c => c.BridgeId).SingleAsync(ct));
    }
}
