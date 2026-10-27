using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Authentication.Devices;
using PrintLogApi.Models.DTOs.Bridges;
using PrintLogApi.Models.DTOs.Connection;
using PrintLogApi.Services;
using Xunit;
using static PrintLogApi.IntegrationTests.Bridges.BridgeTestKit;
using static PrintLogApi.IntegrationTests.Devices.DeviceTestKit;

namespace PrintLogApi.IntegrationTests.Bridges;

/// <summary>
/// The bridge's check-in (printlog-bridge#100): it reports itself and the printers it found, and
/// gets back the printers the user chose for it.
/// </summary>
public class BridgeCheckInTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private Task<HttpResponseMessage> Send(HttpRequestMessage request) => _client.SendAsync(request, TestContext.Current.CancellationToken);

    [Fact]
    public async Task TheFirstCheckIn_RegistersTheBridge_WithNoDesiredPrinters()
    {
        var bridge = await PairBridgeAsync(_client, "first");

        var body = await CheckInOkAsync(_client, bridge.Token, CheckInBody(Bambu()));

        Assert.Equal(bridge.GrantId, body.GetProperty("bridgeId").GetInt64());
        Assert.Empty(body.GetProperty("desired").GetProperty("printers").EnumerateArray());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("desired").GetProperty("version").GetString()));
        Assert.True(body.GetProperty("checkInSeconds").GetInt32() > 0);
        Assert.True(body.GetProperty("desiredPollSeconds").GetInt32() > 0);
        Assert.Empty(body.GetProperty("rejected").EnumerateArray());

        var listed = Assert.Single((await GetAsUserAsync(_client, bridge.Subject, "/api/bridges")).EnumerateArray());
        Assert.Equal(bridge.GrantId, listed.GetProperty("id").GetInt64());
        Assert.Equal("printlog-bridge on nas", listed.GetProperty("displayName").GetString());
        Assert.Equal("0.3.0", listed.GetProperty("agentVersion").GetString());
        Assert.Equal("remote", listed.GetProperty("mode").GetString());
        Assert.Equal("nas", listed.GetProperty("hostName").GetString());
        Assert.Equal(["192.168.1.0/24"], listed.GetProperty("networks").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal((int)ConnectionStatus.Online, listed.GetProperty("status").GetInt32());
        Assert.Equal(1, listed.GetProperty("discoveredNotConnectedCount").GetInt32());
        Assert.Equal(0, listed.GetProperty("printerCount").GetInt32());
    }

    [Fact]
    public async Task EachReport_ReplacesTheDiscoveredList_KeepingIdsOfPrintersStillSeen()
    {
        var bridge = await PairBridgeAsync(_client, "replace");
        await CheckInOkAsync(_client, bridge.Token, CheckInBody(Bambu("SERIALA"), Moonraker()));
        var first = await DiscoveredAsync(_client, bridge);
        Assert.Equal(2, first.Length);
        var voron = first.Single(d => d.GetProperty("kind").GetString() == "moonraker");

        // The Voron moved to a new address; the Bambu is gone; a second Bambu appeared.
        await CheckInOkAsync(_client, bridge.Token, CheckInBody(Moonraker(address: "192.168.1.51"), Bambu("SERIALB", "192.168.1.61")));
        var second = await DiscoveredAsync(_client, bridge);

        Assert.Equal(2, second.Length);
        var movedVoron = second.Single(d => d.GetProperty("kind").GetString() == "moonraker");
        Assert.Equal(voron.GetProperty("id").GetInt64(), movedVoron.GetProperty("id").GetInt64());
        Assert.Equal("192.168.1.51", movedVoron.GetProperty("address").GetString());
        Assert.Equal(voron.GetProperty("firstSeenAt").GetDateTimeOffset(), movedVoron.GetProperty("firstSeenAt").GetDateTimeOffset());
        Assert.Equal("SERIALB", second.Single(d => d.GetProperty("kind").GetString() == "bambu").GetProperty("serial").GetString());

        // No list at all keeps what is stored (a bridge with discovery off); an empty one clears it.
        await CheckInOkAsync(_client, bridge.Token, new { agentVersion = "0.3.0" });
        Assert.Equal(2, (await DiscoveredAsync(_client, bridge)).Length);
        await CheckInOkAsync(_client, bridge.Token, CheckInBody());
        Assert.Empty(await DiscoveredAsync(_client, bridge));
    }

    /// <summary>
    /// The heart of #100's "never connect automatically": nothing the bridge sends makes a
    /// printer it found into one it watches. Only the user's connect does.
    /// </summary>
    [Fact]
    public async Task ACheckIn_NeverConnectsAPrinter_WhateverItSends()
    {
        var bridge = await PairBridgeAsync(_client, "never-auto");
        var printerId = await CreatePrinterAsync(_client, bridge.Subject);

        var body = await CheckInOkAsync(_client, bridge.Token, new
        {
            agentVersion = "0.3.0",
            discovered = new[] { Bambu() },
            desired = new { printers = new[] { new { printerId, kind = "bambu", host = "192.168.1.60", serial = "01P00A123456789" } } },
            printers = new[] { new { printerId, kind = "bambu", host = "192.168.1.60" } },
            desiredPrinters = new[] { new { printerId, kind = "bambu", host = "192.168.1.60" } },
        });

        Assert.Empty(body.GetProperty("desired").GetProperty("printers").EnumerateArray());
        Assert.Empty((await GetAsUserAsync(_client, bridge.Subject, $"/api/bridges/{bridge.GrantId}/printers")).EnumerateArray());
        Assert.Equal((int)DiscoveredPrinterState.New, Assert.Single(await DiscoveredAsync(_client, bridge)).GetProperty("state").GetInt32());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        Assert.False(await db.BridgePrinters.AnyAsync(p => p.BridgeId == bridge.GrantId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EntriesThatBreakARule_AreDropped_AndReported_TheRestIsStored()
    {
        var bridge = await PairBridgeAsync(_client, "drop");

        var body = await CheckInOkAsync(_client, bridge.Token, new
        {
            agentVersion = "0.3.0",
            networks = new[] { "192.168.1.77/24", "10.0.0.0/8", "8.8.8.0/24", "fd12:3456:789a:1::/64", "not a network" },
            discovered = new object[]
            {
                Bambu(),
                Bambu("PUBLIC", "8.8.8.8"),
                Moonraker(mac: "not-a-mac"),
                Bambu(),
                new { kind = "toaster", address = "192.168.1.70" },
                new { kind = "moonraker", address = "192.168.1.71", name = "bad\u0007name" },
                new { kind = "moonraker", address = "192.168.1.72", serial = "has spaces" },
                new { kind = "moonraker", address = "fe80::1" },
                new { kind = "moonraker", address = "fd00::12", port = 7125 },
            },
        });

        var rejected = body.GetProperty("rejected").EnumerateArray()
            .Select(r => $"{r.GetProperty("field").GetString()} {r.GetProperty("code").GetString()}")
            .ToList();
        Assert.Equal(
        [
            "networks[1] network_too_large",
            "networks[2] network_not_private",
            "networks[4] invalid_network",
            "discovered[1] address_not_private",
            "discovered[2] invalid_mac",
            "discovered[3] duplicate_printer",
            "discovered[4] invalid_kind",
            "discovered[5] invalid_text",
            "discovered[6] invalid_serial",
            "discovered[7] address_not_private",
        ], rejected);

        var discovered = await DiscoveredAsync(_client, bridge);
        Assert.Equal(["192.168.1.60", "fd00::12"], discovered.Select(d => d.GetProperty("address").GetString()).Order(StringComparer.Ordinal));

        // Normalized to the network's own address.
        var listed = Assert.Single((await GetAsUserAsync(_client, bridge.Subject, "/api/bridges")).EnumerateArray());
        Assert.Equal(["192.168.1.0/24", "fd12:3456:789a:1::/64"], listed.GetProperty("networks").EnumerateArray().Select(n => n.GetString()));
    }

    public static TheoryData<string> BadReports() =>
    [
        // Over the caps.
        JsonSerializer.Serialize(new { discovered = Enumerable.Range(1, 51).Select(i => Bambu($"SERIAL{i}", $"192.168.1.{i}")).ToArray() }),
        JsonSerializer.Serialize(new { networks = Enumerable.Range(1, 17).Select(i => $"192.168.{i}.0/24").ToArray() }),
        JsonSerializer.Serialize(new { discovered = new[] { new { kind = "moonraker", address = "192.168.1.5", macs = Enumerable.Range(1, 9).Select(i => $"b8:27:eb:00:00:0{i}").ToArray() } } }),

        // Scalars a bridge controls fully: too long, or with control characters.
        JsonSerializer.Serialize(new { agentVersion = new string('1', 51) }),
        JsonSerializer.Serialize(new { agentVersion = "0.3.0 beta" }),
        JsonSerializer.Serialize(new { hostName = new string('h', 101) }),
        JsonSerializer.Serialize(new { hostName = "nas\nevil" }),
        JsonSerializer.Serialize(new { mode = "Some Mode!" }),

        // Not even the right shape.
        """{"discovered": "everything"}""",
        """[]""",
    ];

    [Theory]
    [MemberData(nameof(BadReports))]
    public async Task AMalformedReport_Is400_AndStoresNothing(string json)
    {
        var bridge = await PairBridgeAsync(_client, "bad");
        var request = WithDevice(new HttpRequestMessage(HttpMethod.Put, "/api/bridges/me")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        }, bridge.Token);

        var response = await Send(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await GetAsUserAsync(_client, bridge.Subject, "/api/bridges")).EnumerateArray());
    }

    [Fact]
    public async Task CheckingIn_NeedsTheBridgeSyncScope()
    {
        var bridge = await PairBridgeAsync(_client, "no-scope", AllBridgeScopes.Where(s => s != DeviceScopes.BridgeSync));

        var response = await Send(CheckIn(bridge.Token, CheckInBody()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(DeviceTokenErrors.InsufficientScope, await CodeOf(response));
    }

    /// <summary>The bridge is the device grant, so only a device token can say which bridge is calling.</summary>
    [Fact]
    public async Task ASignedInUser_CannotCheckIn()
    {
        var bridge = await PairBridgeAsync(_client, "user-checkin");

        var response = await Send(UserJson(HttpMethod.Put, "/api/bridges/me", bridge.Subject, CheckInBody()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("device_token_required", await CodeOf(response));
    }

    /// <summary>The sync rule: a check-in every few minutes must not flush every cache the user has.</summary>
    [Fact]
    public async Task ACheckIn_DoesNotBumpTheUsersCacheVersion()
    {
        var bridge = await PairBridgeAsync(_client, "cache");
        var userId = await UserIdAsync(factory, bridge.Subject);
        var versions = factory.Services.GetRequiredService<ICacheVersionService>();
        var before = versions.GetUserCacheVersion(userId);

        await CheckInOkAsync(_client, bridge.Token, CheckInBody(Bambu(), Moonraker()));
        await CheckInOkAsync(_client, bridge.Token, CheckInBody(Moonraker()));

        Assert.Equal(before, versions.GetUserCacheVersion(userId));
    }

    [Fact]
    public async Task TheDesiredList_CanBePolled_WithAnETag()
    {
        var bridge = await PairBridgeAsync(_client, "poll");

        var early = await Send(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/bridges/me/desired"), bridge.Token));
        Assert.Equal(HttpStatusCode.NotFound, early.StatusCode);
        Assert.Equal("bridge_not_registered", await CodeOf(early));

        var checkIn = await CheckInOkAsync(_client, bridge.Token, CheckInBody(Bambu()));
        var version = checkIn.GetProperty("desired").GetProperty("version").GetString()!;

        var poll = await Send(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/bridges/me/desired"), bridge.Token));
        Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
        var etag = poll.Headers.ETag!;
        Assert.Equal(version, (await poll.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("version").GetString());

        var unchanged = WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/bridges/me/desired"), bridge.Token);
        unchanged.Headers.IfNoneMatch.Add(etag);
        Assert.Equal(HttpStatusCode.NotModified, (await Send(unchanged)).StatusCode);

        // The user connects the printer: the same tag now gets the new list.
        var discoveredId = Assert.Single(await DiscoveredAsync(_client, bridge)).GetProperty("id").GetInt64();
        var entry = await ConnectAsync(_client, bridge, discoveredId);

        var changed = WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/bridges/me/desired"), bridge.Token);
        changed.Headers.IfNoneMatch.Add(etag);
        var after = await Send(changed);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        var desired = await after.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.NotEqual(version, desired.GetProperty("version").GetString());
        var printer = Assert.Single(desired.GetProperty("printers").EnumerateArray());
        Assert.Equal(entry.GetProperty("id").GetInt64(), printer.GetProperty("id").GetInt64());
        Assert.Equal("bambu", printer.GetProperty("kind").GetString());
        Assert.Equal("192.168.1.60", printer.GetProperty("host").GetString());
        Assert.Equal(8883, printer.GetProperty("port").GetInt32());
        Assert.Equal("01P00A123456789", printer.GetProperty("serial").GetString());
        Assert.Equal("bambu:sn:01P00A123456789", printer.GetProperty("identityKey").GetString());
    }

    /// <summary>A bridge's connections record which bridge they belong to, from its token and never its body.</summary>
    [Fact]
    public async Task AHeartbeat_RecordsTheBridge_OnceTheBridgeHasCheckedIn()
    {
        var bridge = await PairBridgeAsync(_client, "heartbeat");
        var printerId = await CreatePrinterAsync(_client, bridge.Subject);
        var instanceId = Guid.NewGuid().ToString();
        HttpRequestMessage Heartbeat() => WithDevice(new HttpRequestMessage(HttpMethod.Put, $"/api/Connections/{instanceId}")
        {
            Content = JsonContent.Create(new { kind = "moonraker", displayName = "voron", printerId, bridgeId = 999999 }),
        }, bridge.Token);

        var before = await Send(Heartbeat());
        Assert.Equal(HttpStatusCode.Created, before.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await before.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("bridgeId").ValueKind);

        await CheckInOkAsync(_client, bridge.Token, CheckInBody());

        var after = await Send(Heartbeat());
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal(bridge.GrantId, (await after.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("bridgeId").GetInt64());

        var listed = Assert.Single((await GetAsUserAsync(_client, bridge.Subject, "/api/bridges")).EnumerateArray());
        Assert.Equal(1, listed.GetProperty("connectionCount").GetInt32());
    }

    [Fact]
    public async Task ATooEarlySweepTime_IsClampedToNow()
    {
        var bridge = await PairBridgeAsync(_client, "sweep");

        await CheckInOkAsync(_client, bridge.Token, new { lastSweepAt = DateTimeOffset.UtcNow.AddDays(3) });

        var listed = Assert.Single((await GetAsUserAsync(_client, bridge.Subject, "/api/bridges")).EnumerateArray());
        Assert.InRange(listed.GetProperty("lastSweepAt").GetDateTimeOffset(), DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(1));
    }
}
