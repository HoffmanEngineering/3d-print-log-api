using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Authentication.Devices;
using Xunit;
using static PrintLogApi.IntegrationTests.Devices.DeviceTestKit;

namespace PrintLogApi.IntegrationTests.Bridges;

/// <summary>
/// A paired bridge (printlog-bridge#100): a device grant with <c>bridge:sync</c>, checking in the
/// way printlog-bridge will.
/// </summary>
public static class BridgeTestKit
{
    public static readonly string[] AllBridgeScopes = [.. DeviceScopes.All];

    public sealed record PairedBridge(string Subject, string Token, long GrantId);

    public static async Task<PairedBridge> PairBridgeAsync(HttpClient client, string label, IEnumerable<string>? scopes = null)
    {
        var subject = NewSubject(label);
        var paired = await PairAsync(client, subject, scopes ?? AllBridgeScopes, name: "printlog-bridge on nas");
        return new PairedBridge(subject, paired.GetProperty("token").GetString()!, paired.GetProperty("id").GetInt64());
    }

    /// <summary>A discovered Bambu printer with a serial, the most common stable identity.</summary>
    public static object Bambu(string serial = "01P00A123456789", string address = "192.168.1.60") => new
    {
        kind = "bambu",
        address,
        port = 8883,
        model = "X1 Carbon",
        name = "Workshop X1C",
        serial,
        macs = Array.Empty<string>(),
        configured = false,
    };

    /// <summary>A discovered Moonraker printer, identified by its MACs.</summary>
    public static object Moonraker(string mac = "b8:27:eb:12:34:56", string address = "192.168.1.50", int port = 7125) => new
    {
        kind = "moonraker",
        address,
        port,
        model = "Voron 2.4",
        name = "voron",
        macs = new[] { mac },
        configured = false,
    };

    public static object CheckInBody(params object[] discovered) => new
    {
        agentVersion = "0.3.0",
        mode = "remote",
        hostName = "nas",
        networks = new[] { "192.168.1.0/24" },
        discovered,
    };

    public static HttpRequestMessage CheckIn(string token, object body)
        => WithDevice(new HttpRequestMessage(HttpMethod.Put, "/api/bridges/me") { Content = JsonContent.Create(body) }, token);

    public static async Task<JsonElement> CheckInOkAsync(HttpClient client, string token, object body)
    {
        var response = await client.SendAsync(CheckIn(token, body), TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"check-in failed: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public static async Task<JsonElement> GetAsUserAsync(HttpClient client, string subject, string url)
    {
        var response = await client.SendAsync(AsUser(new HttpRequestMessage(HttpMethod.Get, url), subject), TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url} failed: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public static HttpRequestMessage UserJson(HttpMethod method, string url, string subject, object? body = null)
    {
        var request = AsUser(new HttpRequestMessage(method, url), subject);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    /// <summary>The discovered printers the user sees for a bridge.</summary>
    public static async Task<JsonElement[]> DiscoveredAsync(HttpClient client, PairedBridge bridge)
        => (await GetAsUserAsync(client, bridge.Subject, $"/api/bridges/{bridge.GrantId}/discovered-printers")).EnumerateArray().ToArray();

    /// <summary>Connects a discovered printer to a new 3D Print Log printer and returns the desired entry.</summary>
    public static async Task<JsonElement> ConnectAsync(HttpClient client, PairedBridge bridge, long discoveredId, long? printerId = null)
    {
        printerId ??= await CreatePrinterAsync(client, bridge.Subject);
        var response = await client.SendAsync(
            UserJson(HttpMethod.Post, $"/api/bridges/{bridge.GrantId}/printers", bridge.Subject, new { discoveredPrinterId = discoveredId, printerId }),
            TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"connect failed: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public static async Task<long> UserIdAsync(CustomWebApplicationFactory factory, string subject)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.Users.Where(u => u.OAuthUserId == subject).Select(u => u.Id).SingleAsync(TestContext.Current.CancellationToken);
    }
}
