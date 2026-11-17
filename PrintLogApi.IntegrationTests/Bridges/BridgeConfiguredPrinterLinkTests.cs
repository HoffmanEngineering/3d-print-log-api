using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models.DTOs.Bridges;
using PrintLogApi.Services.Bridges;
using Xunit;
using static PrintLogApi.IntegrationTests.Bridges.BridgeTestKit;
using static PrintLogApi.IntegrationTests.Devices.DeviceTestKit;

namespace PrintLogApi.IntegrationTests.Bridges;

/// <summary>
/// A printer the bridge watches from its config file names the 3D Print Log printer it logs to
/// (<c>printerId</c>), so the Bridges page can show and link it. Kept only when it is one of the
/// owner's printers; anything else is dropped silently, never a refused check-in.
/// </summary>
public class BridgeConfiguredPrinterLinkTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static object Configured(long? printerId, string mac = "b8:27:eb:aa:bb:01", bool configured = true) => new
    {
        kind = "moonraker",
        address = "192.168.1.50",
        port = 7125,
        name = "voron",
        macs = new[] { mac },
        configured,
        printerId,
    };

    private async Task<JsonElement> OnlyDiscoveredAsync(PairedBridge bridge) => Assert.Single(await DiscoveredAsync(_client, bridge));

    private async Task<string> PrinterNameAsync(string subject, long printerId)
        => (await GetAsUserAsync(_client, subject, $"/api/Printers/{printerId}")).GetProperty("name").GetString()!;

    [Fact]
    public async Task AConfiguredPrinter_WithTheOwnersPrinter_IsLinked_WithItsName()
    {
        var bridge = await PairBridgeAsync(_client, "link-own");
        var printerId = await CreatePrinterAsync(_client, bridge.Subject);

        var body = await CheckInOkAsync(_client, bridge.Token, CheckInBody(Configured(printerId)));

        Assert.Empty(body.GetProperty("rejected").EnumerateArray());
        var found = await OnlyDiscoveredAsync(bridge);
        Assert.Equal((int)DiscoveredPrinterState.Configured, found.GetProperty("state").GetInt32());
        Assert.Equal(printerId, found.GetProperty("printerId").GetInt64());
        Assert.Equal(await PrinterNameAsync(bridge.Subject, printerId), found.GetProperty("printerName").GetString());
    }

    [Fact]
    public async Task AnotherUsersPrinter_IsNotLinked_AndTheCheckInStillSucceeds()
    {
        var bridge = await PairBridgeAsync(_client, "link-other");
        var stranger = await PairBridgeAsync(_client, "link-stranger");
        var theirPrinter = await CreatePrinterAsync(_client, stranger.Subject);

        var body = await CheckInOkAsync(_client, bridge.Token, CheckInBody(Configured(theirPrinter)));

        Assert.Empty(body.GetProperty("rejected").EnumerateArray());
        var found = await OnlyDiscoveredAsync(bridge);
        Assert.Equal((int)DiscoveredPrinterState.Configured, found.GetProperty("state").GetInt32());
        Assert.Equal(JsonValueKind.Null, found.GetProperty("printerId").ValueKind);
        Assert.Equal(JsonValueKind.Null, found.GetProperty("printerName").ValueKind);
    }

    [Fact]
    public async Task APrinterIdOnAnEntryThatIsNotConfigured_IsIgnored()
    {
        var bridge = await PairBridgeAsync(_client, "link-not-configured");
        var printerId = await CreatePrinterAsync(_client, bridge.Subject);

        await CheckInOkAsync(_client, bridge.Token, CheckInBody(Configured(printerId, configured: false)));

        var found = await OnlyDiscoveredAsync(bridge);
        Assert.Equal((int)DiscoveredPrinterState.New, found.GetProperty("state").GetInt32());
        Assert.Equal(JsonValueKind.Null, found.GetProperty("printerId").ValueKind);
        Assert.Equal(JsonValueKind.Null, found.GetProperty("printerName").ValueKind);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    [InlineData(long.MaxValue)]
    public async Task AnInvalidOrUnknownPrinterId_IsNotLinked(long printerId)
    {
        var bridge = await PairBridgeAsync(_client, "link-invalid");

        var body = await CheckInOkAsync(_client, bridge.Token, CheckInBody(Configured(printerId)));

        Assert.Empty(body.GetProperty("rejected").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, (await OnlyDiscoveredAsync(bridge)).GetProperty("printerId").ValueKind);
    }

    [Fact]
    public async Task ADeletedPrinter_KeepsItsId_ButHasNoName_UntilTheNextCheckInDropsIt()
    {
        var bridge = await PairBridgeAsync(_client, "link-deleted");
        var printerId = await CreatePrinterAsync(_client, bridge.Subject);
        await CheckInOkAsync(_client, bridge.Token, CheckInBody(Configured(printerId)));

        var deleted = await _client.SendAsync(UserJson(HttpMethod.Delete, $"/api/Printers/{printerId}", bridge.Subject), TestContext.Current.CancellationToken);
        Assert.True(deleted.IsSuccessStatusCode, $"delete failed: {(int)deleted.StatusCode}");

        var found = await OnlyDiscoveredAsync(bridge);
        Assert.Equal(printerId, found.GetProperty("printerId").GetInt64());
        Assert.Equal(JsonValueKind.Null, found.GetProperty("printerName").ValueKind);

        // The bridge still names it on its next check-in; the printer is gone, so it isn't kept.
        await CheckInOkAsync(_client, bridge.Token, CheckInBody(Configured(printerId)));
        Assert.Equal(JsonValueKind.Null, (await OnlyDiscoveredAsync(bridge)).GetProperty("printerId").ValueKind);
    }

    [Fact]
    public async Task ARenamedPrinter_ShowsItsCurrentName()
    {
        var bridge = await PairBridgeAsync(_client, "link-renamed");
        var printerId = await CreatePrinterAsync(_client, bridge.Subject);
        await CheckInOkAsync(_client, bridge.Token, CheckInBody(Configured(printerId)));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            await db.Printers.Where(p => p.Id == printerId)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.Name, "Renamed Voron"), TestContext.Current.CancellationToken);
        }

        Assert.Equal("Renamed Voron", (await OnlyDiscoveredAsync(bridge)).GetProperty("printerName").GetString());
    }

    [Fact]
    public async Task ABridgeThatSendsNoPrinterId_BehavesAsBefore()
    {
        var bridge = await PairBridgeAsync(_client, "link-none");
        var printerId = await CreatePrinterAsync(_client, bridge.Subject);
        await CheckInOkAsync(_client, bridge.Token, CheckInBody(Configured(printerId)));

        // An older bridge (or a config section without printer_id) sends no printerId: the
        // report replaces the stored one, like every other field of the entry.
        await CheckInOkAsync(_client, bridge.Token, CheckInBody(new
        {
            kind = "moonraker",
            address = "192.168.1.50",
            port = 7125,
            name = "voron",
            macs = new[] { "b8:27:eb:aa:bb:01" },
            configured = true,
        }));

        var found = await OnlyDiscoveredAsync(bridge);
        Assert.Equal((int)DiscoveredPrinterState.Configured, found.GetProperty("state").GetInt32());
        Assert.Equal(JsonValueKind.Null, found.GetProperty("printerId").ValueKind);
        Assert.Equal(JsonValueKind.Null, found.GetProperty("printerName").ValueKind);
    }

    [Theory]
    [InlineData(true, 12L, 12L)]
    [InlineData(true, 0L, null)]
    [InlineData(true, -1L, null)]
    [InlineData(true, null, null)]
    [InlineData(false, 12L, null)]
    public void TheValidator_KeepsAPositivePrinterId_OnlyOnAConfiguredEntry(bool configured, long? sent, long? kept)
    {
        var (checkIn, error) = BridgeCheckInValidator.Validate(new BridgeCheckInDto
        {
            Discovered =
            [
                new DiscoveredPrinterReportDto { Kind = "moonraker", Address = "192.168.1.50", Port = 7125, Configured = configured, PrinterId = sent },
            ],
        });

        Assert.Null(error);
        Assert.Equal(kept, Assert.Single(checkIn!.Discovered!).PrinterId);
    }
}
