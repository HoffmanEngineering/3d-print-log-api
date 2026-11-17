using System.Net;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using PrintLogApi.IntegrationTests.Mcp;
using PrintLogApi.IntegrationTests.Spoolman;
using Xunit;
using static PrintLogApi.IntegrationTests.Bridges.BridgeTestKit;
using static PrintLogApi.IntegrationTests.Devices.DeviceTestKit;

namespace PrintLogApi.IntegrationTests.Bridges;

/// <summary>
/// A sealed credential goes to its bridge and nowhere else (story 14a): not to the owner's own
/// endpoints, the sync snapshot, MCP, logs at every level, or telemetry.
/// </summary>
public class SealedCredentialLeakTests(SealedCredentialLeakTests.Factory factory) : IClassFixture<SealedCredentialLeakTests.Factory>
{
    public class Factory : McpDataWebApplicationFactory
    {
        public RecordingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Trace",
                ["Logging:LogLevel:Microsoft"] = "Trace",
            }));
            builder.ConfigureLogging(logging => logging.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
        }
    }

    private readonly HttpClient _client = factory.CreateClient();

    private Task<HttpResponseMessage> Send(HttpRequestMessage request) => _client.SendAsync(request, TestContext.Current.CancellationToken);

    private async Task<string> BodyAsUser(string subject, string url)
    {
        var response = await Send(AsUser(new HttpRequestMessage(HttpMethod.Get, url), subject));
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url}: {(int)response.StatusCode} {text}");
        return text;
    }

    [Fact]
    public async Task ASealedValue_ReachesOnlyItsBridge()
    {
        var bridge = await PairBridgeAsync(_client, "leak");
        using var key = new BridgeSealedCredentialTests.BridgeKey();
        await CheckInOkAsync(_client, bridge.Token, new { publicKey = key.PublicKey, discovered = new[] { Bambu(serial: "LEAK" + Guid.NewGuid().ToString("N")[..10]) } });
        var entry = await ConnectAsync(_client, bridge, Assert.Single(await DiscoveredAsync(_client, bridge)).GetProperty("id").GetInt64());
        var entryId = entry.GetProperty("id").GetInt64();
        var printerId = entry.GetProperty("printerId").GetInt64();
        var sealedValue = CredentialSealer.Seal(key.PublicKey, CredentialSealer.AssociatedData(bridge.GrantId, entryId, "access_code"), "12345678");
        // Any long run of it would be a leak too, such as a log line that truncates.
        var probe = sealedValue[3..40];

        var put = await Send(UserJson(HttpMethod.Put, $"/api/bridges/{bridge.GrantId}/printers/{entryId}/secrets/access_code", bridge.Subject,
            new { @sealed = sealedValue, keyFingerprint = key.Fingerprint }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.DoesNotContain(probe, await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // The bridge gets it, so the probe is right.
        var desired = await Send(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/bridges/me/desired"), bridge.Token));
        Assert.Contains(probe, await desired.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains(probe, JsonSerializer.Serialize(await CheckInOkAsync(_client, bridge.Token, new { })));

        // The owner's own views, the sync snapshot, and the printer and connection endpoints.
        foreach (var url in new[]
                 {
                     "/api/bridges", $"/api/bridges/{bridge.GrantId}", $"/api/bridges/{bridge.GrantId}/printers",
                     $"/api/bridges/{bridge.GrantId}/discovered-printers", "/api/sync/snapshot", $"/api/Printers/{printerId}",
                 })
        {
            Assert.DoesNotContain(probe, await BodyAsUser(bridge.Subject, url));
        }

        // MCP.
        await using (var mcp = await factory.ConnectAsync(bridge.Subject))
        {
            foreach (var (tool, args) in new (string, Dictionary<string, object?>)[]
                     {
                         ("list_printers", new() { ["pageSize"] = 100 }),
                         ("get_printer", new() { ["id"] = printerId }),
                     })
            {
                var result = await mcp.CallToolAsync(tool, args, cancellationToken: TestContext.Current.CancellationToken);
                var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                Assert.False(string.IsNullOrEmpty(text), tool);
                Assert.DoesNotContain(probe, text);
            }
        }

        // Logs at every level, and everything telemetry would have shipped.
        Assert.DoesNotContain(factory.Logs.Messages, m => m.Contains(probe, StringComparison.Ordinal));
        var telemetry = Encoding.UTF8.GetString(Microsoft.ApplicationInsights.Extensibility.Implementation.JsonSerializer.Serialize(factory.Telemetry.Items, compress: false));
        Assert.Contains("BridgePrinterSecretSet", telemetry);
        Assert.DoesNotContain(probe, telemetry);
    }
}
