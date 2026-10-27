using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models.DTOs.Bridges;
using PrintLogApi.Models.DTOs.Connection;
using Xunit;
using static PrintLogApi.IntegrationTests.Bridges.BridgeTestKit;
using static PrintLogApi.IntegrationTests.Devices.DeviceTestKit;

namespace PrintLogApi.IntegrationTests.Bridges;

/// <summary>
/// Two bridges (or two entries on one) watching the same printer log every print twice: each has
/// its own instance id, so nothing downstream dedupes them. Connect refuses every such case.
/// Moving a printer between bridges is printlog-bridge#103's; until then the 409 is the answer.
/// </summary>
public class BridgeDuplicateWatchTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private Task<HttpResponseMessage> Send(HttpRequestMessage request) => _client.SendAsync(request, TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> Connect(PairedBridge bridge, object body)
        => Send(UserJson(HttpMethod.Post, $"/api/bridges/{bridge.GrantId}/printers", bridge.Subject, body));

    private static async Task<long?> BridgeIdOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return body.TryGetProperty("bridgeId", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : null;
    }

    [Fact]
    public async Task APrinter_IsWatchedByOneBridgeOnly()
    {
        var (a, b) = await TwoBridgesAsync("printer-twice");
        var printerId = await CreatePrinterAsync(_client, a.Subject);
        Assert.Equal(HttpStatusCode.Created, (await Connect(a, new { kind = "moonraker", host = "192.168.1.5", printerId })).StatusCode);

        var second = await Connect(b, new { kind = "moonraker", host = "192.168.2.5", printerId });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("printer_connected_elsewhere", await CodeOf(second));
        Assert.Equal(a.GrantId, await BridgeIdOf(await Connect(b, new { kind = "moonraker", host = "192.168.2.5", printerId })));
    }

    [Fact]
    public async Task ThePhysicalPrinter_IsWatchedByOneBridgeOnly_ByItsSerial()
    {
        var (a, b) = await TwoBridgesAsync("serial-twice");
        await CheckInOkAsync(_client, a.Token, CheckInBody(Bambu()));
        await CheckInOkAsync(_client, b.Token, CheckInBody(Bambu(address: "192.168.1.61")));
        await ConnectAsync(_client, a, Assert.Single(await DiscoveredAsync(_client, a)).GetProperty("id").GetInt64());

        var second = await Connect(b, new
        {
            discoveredPrinterId = Assert.Single(await DiscoveredAsync(_client, b)).GetProperty("id").GetInt64(),
            printerId = await CreatePrinterAsync(_client, b.Subject),
        });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("printer_connected_elsewhere", await CodeOf(second));
    }

    [Fact]
    public async Task TheSameAddress_CannotBeConnectedTwiceByHand()
    {
        var bridge = await PairBridgeAsync(_client, "host-twice");
        await CheckInOkAsync(_client, bridge.Token, CheckInBody());
        Assert.Equal(HttpStatusCode.Created,
            (await Connect(bridge, new { kind = "moonraker", host = "192.168.1.50", printerId = await CreatePrinterAsync(_client, bridge.Subject) })).StatusCode);

        foreach (var port in new int?[] { null, 7125 })
        {
            var again = await Connect(bridge, new { kind = "moonraker", host = "192.168.1.50", port, printerId = await CreatePrinterAsync(_client, bridge.Subject) });
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Equal("discovered_printer_already_connected", await CodeOf(again));
        }

        var entry = Assert.Single((await GetAsUserAsync(_client, bridge.Subject, $"/api/bridges/{bridge.GrantId}/printers")).EnumerateArray());
        Assert.Equal("moonraker:ip:192.168.1.50:0", entry.GetProperty("identityKey").GetString());
    }

    [Fact]
    public async Task AHandEntry_AndALaterDiscoveryOfItsAddress_Collide()
    {
        var bridge = await PairBridgeAsync(_client, "hand-then-found");
        await CheckInOkAsync(_client, bridge.Token, CheckInBody());
        Assert.Equal(HttpStatusCode.Created,
            (await Connect(bridge, new { kind = "moonraker", host = "192.168.1.50", printerId = await CreatePrinterAsync(_client, bridge.Subject) })).StatusCode);

        await CheckInOkAsync(_client, bridge.Token, CheckInBody(Moonraker()));
        var found = Assert.Single(await DiscoveredAsync(_client, bridge));
        Assert.Equal((int)DiscoveredPrinterState.Connected, found.GetProperty("state").GetInt32());

        var again = await Connect(bridge, new { discoveredPrinterId = found.GetProperty("id").GetInt64(), printerId = await CreatePrinterAsync(_client, bridge.Subject) });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("discovered_printer_already_connected", await CodeOf(again));
    }

    /// <summary>The bridge's config file wins over the app, so connecting such a printer would only watch it twice.</summary>
    [Fact]
    public async Task APrinterInTheBridgesConfigFile_CannotBeConnected_AndShowsAsConfigured()
    {
        var bridge = await PairBridgeAsync(_client, "configured");
        await CheckInOkAsync(_client, bridge.Token, CheckInBody(Bambu()));
        var entry = await ConnectAsync(_client, bridge, Assert.Single(await DiscoveredAsync(_client, bridge)).GetProperty("id").GetInt64());

        // The user also added it to the config file: Configured wins, and the app entry is still named.
        var configured = new Dictionary<string, object?>(JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(Bambu()))!) { ["configured"] = true };
        await CheckInOkAsync(_client, bridge.Token, CheckInBody(configured));
        var found = Assert.Single(await DiscoveredAsync(_client, bridge));
        Assert.Equal((int)DiscoveredPrinterState.Configured, found.GetProperty("state").GetInt32());
        Assert.Equal(entry.GetProperty("id").GetInt64(), found.GetProperty("printerEntryId").GetInt64());

        var other = await PairBridgeAsync(_client, "configured-other");
        var voron = new Dictionary<string, object?>(JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(Moonraker()))!) { ["configured"] = true };
        await CheckInOkAsync(_client, other.Token, CheckInBody(voron));
        var refused = await Connect(other, new
        {
            discoveredPrinterId = Assert.Single(await DiscoveredAsync(_client, other)).GetProperty("id").GetInt64(),
            printerId = await CreatePrinterAsync(_client, other.Subject),
        });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("printer_configured_on_bridge", await CodeOf(refused));
    }

    /// <summary>A bridge whose check-ins are held back by a backfill still looks alive while it polls.</summary>
    [Fact]
    public async Task PollingTheDesiredList_KeepsTheBridgeOnline_AtMostEveryFewMinutes()
    {
        var bridge = await PairBridgeAsync(_client, "poll-alive");
        await CheckInOkAsync(_client, bridge.Token, CheckInBody());
        var ct = TestContext.Current.CancellationToken;

        async Task SetLastSeen(DateTime value)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            await db.Bridges.Where(b => b.Id == bridge.GrantId).ExecuteUpdateAsync(s => s.SetProperty(b => b.LastSeenAt, value), ct);
        }

        async Task Poll() => Assert.Equal(HttpStatusCode.OK,
            (await Send(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/bridges/me/desired"), bridge.Token))).StatusCode);

        await SetLastSeen(DateTime.UtcNow.AddHours(-1));
        Assert.Equal((int)ConnectionStatus.Stale, (await GetAsUserAsync(_client, bridge.Subject, $"/api/bridges/{bridge.GrantId}")).GetProperty("status").GetInt32());
        await Poll();
        var listed = await GetAsUserAsync(_client, bridge.Subject, $"/api/bridges/{bridge.GrantId}");
        Assert.Equal((int)ConnectionStatus.Online, listed.GetProperty("status").GetInt32());

        // Within the throttle window a poll writes nothing.
        var recent = DateTime.UtcNow.AddSeconds(-30);
        await SetLastSeen(recent);
        await Poll();
        Assert.InRange((await GetAsUserAsync(_client, bridge.Subject, $"/api/bridges/{bridge.GrantId}")).GetProperty("lastSeenAt").GetDateTimeOffset(),
            new DateTimeOffset(recent.AddSeconds(-1), TimeSpan.Zero), new DateTimeOffset(recent.AddSeconds(1), TimeSpan.Zero));
    }

    private async Task<(PairedBridge A, PairedBridge B)> TwoBridgesAsync(string label)
    {
        var a = await PairBridgeAsync(_client, label);
        var paired = await PairAsync(_client, a.Subject, AllBridgeScopes, name: "printlog-bridge on workshop");
        var b = new PairedBridge(a.Subject, paired.GetProperty("token").GetString()!, paired.GetProperty("id").GetInt64());
        await CheckInOkAsync(_client, a.Token, CheckInBody());
        await CheckInOkAsync(_client, b.Token, new { networks = new[] { "192.168.2.0/24" } });
        return (a, b);
    }
}
