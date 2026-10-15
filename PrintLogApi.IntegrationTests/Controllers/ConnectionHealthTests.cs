using System.Net;
using System.Text;
using PrintLogApi.Models.DTOs.Connection;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// The health a connector sends with its heartbeat (printlog-bridge#37): software versions, the
/// outbox's backlog and the last problem. Stored on the connection, shown to its owner.
/// </summary>
public class ConnectionHealthTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _httpClient;

    public ConnectionHealthTests(CustomWebApplicationFactory factory)
    {
        _httpClient = factory.CreateClient();
    }

    /// <summary>A heartbeat exactly as printlog-bridge sends it, field names and all.</summary>
    private static string BridgeHeartbeat(long printerId, string health) => $$"""
        {
          "kind": "moonraker",
          "displayName": "printlog-bridge (voron)",
          "agentVersion": "0.4.0",
          "printerId": {{printerId}},
          {{health}}
        }
        """;

    private const string FullHealth = """
        "mode": "remote",
        "klipperVersion": "v0.12.0-123-gabcdef",
        "moonrakerVersion": "v0.9.3-45",
        "printerModel": "InfiMech EX",
        "outboxDepth": 3,
        "outboxOldestAgeSeconds": 600,
        "lastError": "couldn't send the heartbeat: can't reach 3D Print Log at <url>",
        "lastErrorAt": "2026-10-15T12:00:00+00:00",
        "errorCount": 2,
        "snapshots": false,
        "slicerSettings": true
        """;

    [Fact]
    public async Task Put_WithHealth_StoresAndReturnsEveryField()
    {
        var instanceId = NewInstanceId();
        var before = DateTimeOffset.UtcNow;

        var connection = await ReadAsync(await PutJsonAsync(instanceId, BridgeHeartbeat(IntegrationTestSeeder.TestPrinterId, FullHealth)));

        Assert.Equal("remote", connection.Mode);
        Assert.Equal("v0.12.0-123-gabcdef", connection.KlipperVersion);
        Assert.Equal("v0.9.3-45", connection.MoonrakerVersion);
        Assert.Equal("InfiMech EX", connection.PrinterModel);
        Assert.Equal(3, connection.OutboxDepth);
        Assert.NotNull(connection.OutboxOldestQueuedAt);
        var queuedAgo = before - connection.OutboxOldestQueuedAt!.Value;
        Assert.InRange(queuedAgo.TotalSeconds, 590, 610);
        Assert.Equal("couldn't send the heartbeat: can't reach 3D Print Log at <url>", connection.LastError);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero), connection.LastErrorAt);
        Assert.Equal(2, connection.ErrorCount);
        Assert.False(connection.Snapshots);
        Assert.True(connection.SlicerSettings);

        var listed = Assert.Single(await ListAsync(), c => c.InstanceId == instanceId);
        Assert.Equal("v0.12.0-123-gabcdef", listed.KlipperVersion);
        Assert.Equal(3, listed.OutboxDepth);
    }

    [Fact]
    public async Task Put_FromAnOldBridgeWithoutHealth_StillWorksAndKnowsNothing()
    {
        var instanceId = NewInstanceId();

        var response = await PutJsonAsync(instanceId, $$"""
            {"kind": "moonraker", "displayName": "old bridge", "agentVersion": "0.1.0", "printerId": {{IntegrationTestSeeder.TestPrinterId}}}
            """);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var connection = await ReadAsync(response);
        Assert.Null(connection.Mode);
        Assert.Null(connection.KlipperVersion);
        Assert.Null(connection.MoonrakerVersion);
        Assert.Null(connection.PrinterModel);
        Assert.Null(connection.OutboxDepth);
        Assert.Null(connection.OutboxOldestQueuedAt);
        Assert.Null(connection.LastError);
        Assert.Null(connection.LastErrorAt);
        Assert.Null(connection.ErrorCount);
        Assert.Null(connection.Snapshots);
        Assert.Null(connection.SlicerSettings);
    }

    [Fact]
    public async Task Put_EachHeartbeatReplacesTheHealth()
    {
        var instanceId = NewInstanceId();
        await PutJsonAsync(instanceId, BridgeHeartbeat(IntegrationTestSeeder.TestPrinterId, FullHealth));

        var connection = await ReadAsync(await PutJsonAsync(instanceId, BridgeHeartbeat(IntegrationTestSeeder.TestPrinterId, """
            "mode": "remote", "klipperVersion": "v0.13.0", "moonrakerVersion": null, "printerModel": null,
            "outboxDepth": 0, "outboxOldestAgeSeconds": null, "lastError": null, "lastErrorAt": null,
            "errorCount": 0, "snapshots": true, "slicerSettings": false
            """)));

        Assert.Equal("v0.13.0", connection.KlipperVersion);
        Assert.Null(connection.MoonrakerVersion);
        Assert.Equal(0, connection.OutboxDepth);
        Assert.Null(connection.OutboxOldestQueuedAt);
        Assert.Null(connection.LastError);
        Assert.Equal(0, connection.ErrorCount);
    }

    [Fact]
    public async Task Put_HealthIsNeverAReasonToRefuseTheHeartbeat()
    {
        var connection = await ReadAsync(await PutJsonAsync(NewInstanceId(), BridgeHeartbeat(IntegrationTestSeeder.TestPrinterId, $$"""
            "mode": "{{new string('m', 40)}}",
            "klipperVersion": "{{new string('k', 80)}}",
            "moonrakerVersion": "{{new string('r', 80)}}",
            "printerModel": "{{new string('p', 150)}}",
            "outboxDepth": -4,
            "outboxOldestAgeSeconds": -60,
            "lastError": "{{new string('e', 900)}}",
            "lastErrorAt": "2999-01-01T00:00:00+00:00",
            "errorCount": -1
            """)));

        Assert.Equal(20, connection.Mode!.Length);
        Assert.Equal(50, connection.KlipperVersion!.Length);
        Assert.Equal(50, connection.MoonrakerVersion!.Length);
        Assert.Equal(100, connection.PrinterModel!.Length);
        Assert.Equal(500, connection.LastError!.Length);
        Assert.Equal(0, connection.OutboxDepth);
        Assert.True(connection.OutboxOldestQueuedAt <= DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.True(connection.LastErrorAt <= DateTimeOffset.UtcNow.AddSeconds(1), "a future time is the agent's clock, not when it happened");
        Assert.Equal(0, connection.ErrorCount);
    }

    [Fact]
    public async Task Put_BlankStringsAreUnknown()
    {
        var connection = await ReadAsync(await PutJsonAsync(NewInstanceId(), BridgeHeartbeat(IntegrationTestSeeder.TestPrinterId, """
            "mode": " ", "klipperVersion": "", "lastError": "   "
            """)));

        Assert.Null(connection.Mode);
        Assert.Null(connection.KlipperVersion);
        Assert.Null(connection.LastError);
    }

    [Fact]
    public async Task Get_AnotherUsersHealthIsNotShown()
    {
        var instanceId = NewInstanceId();
        await PutJsonAsync(instanceId, BridgeHeartbeat(IntegrationTestSeeder.TestPrinterId, FullHealth));

        var theirs = await SendAsync(HttpMethod.Get, $"/api/Connections/{instanceId}", userOAuthId: IntegrationTestSeeder.SecondaryUserOAuthId);

        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
    }

    private static string NewInstanceId() => Guid.NewGuid().ToString();

    private Task<HttpResponseMessage> PutJsonAsync(string instanceId, string json)
        => SendAsync(HttpMethod.Put, $"/api/Connections/{Uri.EscapeDataString(instanceId)}", json);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? json = null, string userOAuthId = IntegrationTestSeeder.TestUserOAuthId)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, userOAuthId);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<ConnectionDto> ReadAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        return (await response.Content.ReadFromJsonAsync<ConnectionDto>(TestContext.Current.CancellationToken))!;
    }

    private async Task<List<ConnectionDto>> ListAsync()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/Connections");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<ConnectionDto>>(TestContext.Current.CancellationToken))!;
    }
}
