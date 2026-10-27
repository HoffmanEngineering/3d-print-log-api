using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Authentication.Devices;
using PrintLogApi.Models.DTOs.Bridges;
using Xunit;
using static PrintLogApi.IntegrationTests.Bridges.BridgeTestKit;
using static PrintLogApi.IntegrationTests.Devices.DeviceTestKit;

namespace PrintLogApi.IntegrationTests.Bridges;

/// <summary>
/// What the user does with a bridge from the web app (printlog-bridge#100): list it, name it,
/// connect the printers it found, disconnect, ignore, and ask for a scan.
/// </summary>
public class BridgeManagementTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private Task<HttpResponseMessage> Send(HttpRequestMessage request) => _client.SendAsync(request, TestContext.Current.CancellationToken);

    private async Task<(PairedBridge Bridge, long DiscoveredId)> BridgeWithABambu(string label)
    {
        var bridge = await PairBridgeAsync(_client, label);
        await CheckInOkAsync(_client, bridge.Token, CheckInBody(Bambu(), Moonraker()));
        var bambu = (await DiscoveredAsync(_client, bridge)).Single(d => d.GetProperty("kind").GetString() == "bambu");
        return (bridge, bambu.GetProperty("id").GetInt64());
    }

    private async Task<string> VersionAsync(PairedBridge bridge)
        => (await CheckInOkAsync(_client, bridge.Token, new { })).GetProperty("desired").GetProperty("version").GetString()!;

    [Fact]
    public async Task ConnectingADiscoveredPrinter_AddsItToTheDesiredList()
    {
        var (bridge, discoveredId) = await BridgeWithABambu("connect");
        var printerId = await CreatePrinterAsync(_client, bridge.Subject);
        var version = await VersionAsync(bridge);

        var entry = await ConnectAsync(_client, bridge, discoveredId, printerId);

        Assert.Equal(printerId, entry.GetProperty("printerId").GetInt64());
        Assert.Equal("bambu", entry.GetProperty("kind").GetString());
        Assert.Equal("192.168.1.60", entry.GetProperty("host").GetString());
        Assert.False(string.IsNullOrEmpty(entry.GetProperty("printerName").GetString()));

        var checkIn = await CheckInOkAsync(_client, bridge.Token, new { });
        Assert.NotEqual(version, checkIn.GetProperty("desired").GetProperty("version").GetString());
        var desired = Assert.Single(checkIn.GetProperty("desired").GetProperty("printers").EnumerateArray());
        Assert.Equal(printerId, desired.GetProperty("printerId").GetInt64());

        var bambu = (await DiscoveredAsync(_client, bridge)).Single(d => d.GetProperty("id").GetInt64() == discoveredId);
        Assert.Equal((int)DiscoveredPrinterState.Connected, bambu.GetProperty("state").GetInt32());
        Assert.Equal(entry.GetProperty("id").GetInt64(), bambu.GetProperty("printerEntryId").GetInt64());

        var listed = await GetAsUserAsync(_client, bridge.Subject, $"/api/bridges/{bridge.GrantId}");
        Assert.Equal(1, listed.GetProperty("printerCount").GetInt32());
        Assert.Equal(1, listed.GetProperty("discoveredNotConnectedCount").GetInt32());
    }

    [Fact]
    public async Task APrinterCanBeConnectedByHand_WhenDiscoveryCannotSeeIt()
    {
        var bridge = await PairBridgeAsync(_client, "manual");
        await CheckInOkAsync(_client, bridge.Token, CheckInBody());
        var printerId = await CreatePrinterAsync(_client, bridge.Subject);

        var response = await Send(UserJson(HttpMethod.Post, $"/api/bridges/{bridge.GrantId}/printers", bridge.Subject,
            new { kind = "prusalink", host = "mk4.local", port = 80, printerId }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var entry = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("mk4.local", entry.GetProperty("host").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("identityKey").ValueKind);
    }

    public static TheoryData<string, string> BadConnects() => new()
    {
        { """{"kind":"moonraker","host":"8.8.8.8"}""", "invalid_host" },
        { """{"kind":"moonraker","host":"printer.example.com"}""", "invalid_host" },
        { """{"kind":"moonraker","host":"http://192.168.1.5"}""", "invalid_host" },
        { """{"kind":"moonraker"}""", "invalid_host" },
        { """{"kind":"toaster","host":"192.168.1.5"}""", "invalid_kind" },
        { """{"kind":"bambu","host":"192.168.1.5"}""", "serial_required" },
        { """{"kind":"moonraker","host":"192.168.1.5","port":70000}""", "invalid_port" },
        { """{"kind":"moonraker","host":"192.168.1.5","serial":"has spaces"}""", "invalid_serial" },
        { """{"discoveredPrinterId":999999999}""", "discovered_printer_not_found" },
    };

    [Theory]
    [MemberData(nameof(BadConnects))]
    public async Task ABadConnect_Is400_WithACode(string partial, string code)
    {
        var bridge = await PairBridgeAsync(_client, "bad-connect");
        await CheckInOkAsync(_client, bridge.Token, CheckInBody());
        var printerId = await CreatePrinterAsync(_client, bridge.Subject);
        var body = JsonSerializer.Deserialize<Dictionary<string, object>>(partial)!;
        body["printerId"] = printerId;

        var response = await Send(UserJson(HttpMethod.Post, $"/api/bridges/{bridge.GrantId}/printers", bridge.Subject, body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, await CodeOf(response));
    }

    [Fact]
    public async Task APrinter_IsConnectedOncePerBridge_AndMustBeYours()
    {
        var (bridge, discoveredId) = await BridgeWithABambu("twice");
        var printerId = await CreatePrinterAsync(_client, bridge.Subject);
        await ConnectAsync(_client, bridge, discoveredId, printerId);

        var samePrinter = await Send(UserJson(HttpMethod.Post, $"/api/bridges/{bridge.GrantId}/printers", bridge.Subject,
            new { kind = "moonraker", host = "192.168.1.99", printerId }));
        Assert.Equal(HttpStatusCode.Conflict, samePrinter.StatusCode);
        Assert.Equal("printer_already_connected", await CodeOf(samePrinter));

        var sameDiscovery = await Send(UserJson(HttpMethod.Post, $"/api/bridges/{bridge.GrantId}/printers", bridge.Subject,
            new { discoveredPrinterId = discoveredId, printerId = await CreatePrinterAsync(_client, bridge.Subject) }));
        Assert.Equal(HttpStatusCode.Conflict, sameDiscovery.StatusCode);
        Assert.Equal("discovered_printer_already_connected", await CodeOf(sameDiscovery));

        var foreignPrinter = await Send(UserJson(HttpMethod.Post, $"/api/bridges/{bridge.GrantId}/printers", bridge.Subject,
            new { kind = "moonraker", host = "192.168.1.98", printerId = IntegrationTestSeeder.TestPrinterId }));
        Assert.Equal(HttpStatusCode.BadRequest, foreignPrinter.StatusCode);
        Assert.Equal("printer_not_found", await CodeOf(foreignPrinter));
    }

    [Fact]
    public async Task Disconnecting_RemovesThePrinterFromTheDesiredList()
    {
        var (bridge, discoveredId) = await BridgeWithABambu("disconnect");
        var entry = await ConnectAsync(_client, bridge, discoveredId);
        var version = await VersionAsync(bridge);

        var response = await Send(UserJson(HttpMethod.Delete, $"/api/bridges/{bridge.GrantId}/printers/{entry.GetProperty("id").GetInt64()}", bridge.Subject));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var checkIn = await CheckInOkAsync(_client, bridge.Token, new { });
        Assert.Empty(checkIn.GetProperty("desired").GetProperty("printers").EnumerateArray());
        Assert.NotEqual(version, checkIn.GetProperty("desired").GetProperty("version").GetString());
        Assert.Equal(HttpStatusCode.NotFound,
            (await Send(UserJson(HttpMethod.Delete, $"/api/bridges/{bridge.GrantId}/printers/{entry.GetProperty("id").GetInt64()}", bridge.Subject))).StatusCode);
    }

    [Fact]
    public async Task IgnoringAPrinter_HidesItFromTheCount_AndSurvivesItDisappearing()
    {
        var (bridge, discoveredId) = await BridgeWithABambu("ignore");

        var ignore = await Send(UserJson(HttpMethod.Post, $"/api/bridges/{bridge.GrantId}/discovered-printers/{discoveredId}/ignore", bridge.Subject));
        Assert.Equal(HttpStatusCode.NoContent, ignore.StatusCode);
        Assert.Equal(1, (await GetAsUserAsync(_client, bridge.Subject, $"/api/bridges/{bridge.GrantId}")).GetProperty("discoveredNotConnectedCount").GetInt32());

        // Gone for a check-in, then back: still ignored, under a new row.
        await CheckInOkAsync(_client, bridge.Token, CheckInBody(Moonraker()));
        await CheckInOkAsync(_client, bridge.Token, CheckInBody(Moonraker(), Bambu()));
        var bambu = (await DiscoveredAsync(_client, bridge)).Single(d => d.GetProperty("kind").GetString() == "bambu");
        Assert.Equal((int)DiscoveredPrinterState.Ignored, bambu.GetProperty("state").GetInt32());

        // Ignoring is never connecting.
        Assert.Empty((await CheckInOkAsync(_client, bridge.Token, new { })).GetProperty("desired").GetProperty("printers").EnumerateArray());

        var unignore = await Send(UserJson(HttpMethod.Delete, $"/api/bridges/{bridge.GrantId}/discovered-printers/{bambu.GetProperty("id").GetInt64()}/ignore", bridge.Subject));
        Assert.Equal(HttpStatusCode.NoContent, unignore.StatusCode);
        Assert.Equal(2, (await GetAsUserAsync(_client, bridge.Subject, $"/api/bridges/{bridge.GrantId}")).GetProperty("discoveredNotConnectedCount").GetInt32());
    }

    [Fact]
    public async Task ABridgeCanBeNamed_AndLabelled()
    {
        var bridge = await PairBridgeAsync(_client, "name");
        await CheckInOkAsync(_client, bridge.Token, CheckInBody());

        var patch = await Send(UserJson(HttpMethod.Patch, $"/api/bridges/{bridge.GrantId}", bridge.Subject, new { name = "Garage NAS", location = "Workshop" }));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var listed = await GetAsUserAsync(_client, bridge.Subject, $"/api/bridges/{bridge.GrantId}");
        Assert.Equal("Garage NAS", listed.GetProperty("name").GetString());
        Assert.Equal("Garage NAS", listed.GetProperty("displayName").GetString());
        Assert.Equal("printlog-bridge on nas", listed.GetProperty("deviceName").GetString());
        Assert.Equal("Workshop", listed.GetProperty("location").GetString());

        // Null keeps a field; an empty string clears it.
        await Send(UserJson(HttpMethod.Patch, $"/api/bridges/{bridge.GrantId}", bridge.Subject, new { name = "" }));
        listed = await GetAsUserAsync(_client, bridge.Subject, $"/api/bridges/{bridge.GrantId}");
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("name").ValueKind);
        Assert.Equal("printlog-bridge on nas", listed.GetProperty("displayName").GetString());
        Assert.Equal("Workshop", listed.GetProperty("location").GetString());

        var tooLong = await Send(UserJson(HttpMethod.Patch, $"/api/bridges/{bridge.GrantId}", bridge.Subject, new { location = new string('x', 101) }));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task ScanNow_IsPassedToTheBridge()
    {
        var bridge = await PairBridgeAsync(_client, "scan");
        var version = (await CheckInOkAsync(_client, bridge.Token, CheckInBody())).GetProperty("desired").GetProperty("version").GetString();

        var scan = await Send(UserJson(HttpMethod.Post, $"/api/bridges/{bridge.GrantId}/scan", bridge.Subject));
        Assert.Equal(HttpStatusCode.Accepted, scan.StatusCode);

        var desired = (await CheckInOkAsync(_client, bridge.Token, new { })).GetProperty("desired");
        Assert.NotEqual(version, desired.GetProperty("version").GetString());
        var requestedAt = desired.GetProperty("scanRequestedAt").GetDateTimeOffset();

        // A second tap within the minute is the same request.
        Assert.Equal(HttpStatusCode.Accepted, (await Send(UserJson(HttpMethod.Post, $"/api/bridges/{bridge.GrantId}/scan", bridge.Subject))).StatusCode);
        var again = (await CheckInOkAsync(_client, bridge.Token, new { })).GetProperty("desired");
        Assert.Equal(desired.GetProperty("version").GetString(), again.GetProperty("version").GetString());
        Assert.Equal(requestedAt, again.GetProperty("scanRequestedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task AnotherUsersBridge_IsNotFound()
    {
        var (bridge, discoveredId) = await BridgeWithABambu("foreign");
        var stranger = (await PairBridgeAsync(_client, "stranger")).Subject;
        var otherPrinter = await CreatePrinterAsync(_client, stranger);
        Assert.Empty((await GetAsUserAsync(_client, stranger, "/api/bridges")).EnumerateArray());
        foreach (var request in new[]
        {
            UserJson(HttpMethod.Get, $"/api/bridges/{bridge.GrantId}", stranger),
            UserJson(HttpMethod.Get, $"/api/bridges/{bridge.GrantId}/discovered-printers", stranger),
            UserJson(HttpMethod.Get, $"/api/bridges/{bridge.GrantId}/printers", stranger),
            UserJson(HttpMethod.Patch, $"/api/bridges/{bridge.GrantId}", stranger, new { name = "mine" }),
            UserJson(HttpMethod.Post, $"/api/bridges/{bridge.GrantId}/printers", stranger, new { discoveredPrinterId = discoveredId, printerId = otherPrinter }),
            UserJson(HttpMethod.Post, $"/api/bridges/{bridge.GrantId}/discovered-printers/{discoveredId}/ignore", stranger),
            UserJson(HttpMethod.Post, $"/api/bridges/{bridge.GrantId}/scan", stranger),
        })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await Send(request)).StatusCode);
        }

        Assert.Equal((int)DiscoveredPrinterState.New, (await DiscoveredAsync(_client, bridge)).Single(d => d.GetProperty("id").GetInt64() == discoveredId).GetProperty("state").GetInt32());
    }

    /// <summary>A device can report and read its own desired list; managing it is the user's alone.</summary>
    [Fact]
    public async Task ADeviceToken_CannotManageBridges()
    {
        var (bridge, discoveredId) = await BridgeWithABambu("device-manage");
        var printerId = await CreatePrinterAsync(_client, bridge.Subject);

        foreach (var request in new[]
        {
            new HttpRequestMessage(HttpMethod.Get, "/api/bridges"),
            new HttpRequestMessage(HttpMethod.Post, $"/api/bridges/{bridge.GrantId}/printers") { Content = JsonContent.Create(new { discoveredPrinterId = discoveredId, printerId }) },
            new HttpRequestMessage(HttpMethod.Post, $"/api/bridges/{bridge.GrantId}/discovered-printers/{discoveredId}/ignore"),
            new HttpRequestMessage(HttpMethod.Patch, $"/api/bridges/{bridge.GrantId}") { Content = JsonContent.Create(new { name = "x" }) },
        })
        {
            var response = await Send(WithDevice(request, bridge.Token));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(DeviceTokenErrors.EndpointNotAllowed, await CodeOf(response));
        }

        Assert.Empty((await CheckInOkAsync(_client, bridge.Token, new { })).GetProperty("desired").GetProperty("printers").EnumerateArray());
    }

    /// <summary>
    /// Revoking a paired bridge forgets it: the bridge record, what it found and what it was told
    /// to watch. Its connections and their prints stay, unlinked.
    /// </summary>
    [Fact]
    public async Task RevokingTheDevice_DeletesTheBridge_AndUnlinksItsConnections()
    {
        var (bridge, discoveredId) = await BridgeWithABambu("revoke");
        var entry = await ConnectAsync(_client, bridge, discoveredId);
        var instanceId = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.Created, (await Send(WithDevice(new HttpRequestMessage(HttpMethod.Put, $"/api/Connections/{instanceId}")
        {
            Content = JsonContent.Create(new { kind = "bambu", displayName = "X1C", printerId = entry.GetProperty("printerId").GetInt64() }),
        }, bridge.Token))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await Send(UserJson(HttpMethod.Delete, $"/api/device-grants/{bridge.GrantId}", bridge.Subject))).StatusCode);

        Assert.Empty((await GetAsUserAsync(_client, bridge.Subject, "/api/bridges")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(UserJson(HttpMethod.Get, $"/api/bridges/{bridge.GrantId}", bridge.Subject))).StatusCode);

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
    public async Task DeletingAPrinter_RemovesItFromTheDesiredList()
    {
        var (bridge, discoveredId) = await BridgeWithABambu("printer-delete");
        var entry = await ConnectAsync(_client, bridge, discoveredId);
        var version = await VersionAsync(bridge);

        var delete = await Send(UserJson(HttpMethod.Delete, $"/api/Printers/{entry.GetProperty("printerId").GetInt64()}", bridge.Subject));
        Assert.True(delete.IsSuccessStatusCode, $"printer delete: {(int)delete.StatusCode}");

        var desired = (await CheckInOkAsync(_client, bridge.Token, new { })).GetProperty("desired");
        Assert.Empty(desired.GetProperty("printers").EnumerateArray());
        Assert.NotEqual(version, desired.GetProperty("version").GetString());
    }
}
