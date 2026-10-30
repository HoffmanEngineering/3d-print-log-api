using System.Net;
using PrintLogApi.Models.DTOs.Connection;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// A connection tells its agent when a newer release is out (printlog-bridge#39): the check-in
/// response carries the latest version, so Docker and remote-mode bridges hear about it without
/// calling anyone else.
/// </summary>
public class ConnectionUpdateNoticeTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string TagsPath = "/repos/HoffmanEngineering/printlog-bridge/tags?per_page=100";

    private readonly HttpClient _httpClient;

    public ConnectionUpdateNoticeTests(CustomWebApplicationFactory factory)
    {
        // Seeded before any request in this host, since the catalog keeps what it reads.
        factory.GitHub.Set(TagsPath, """[{"name":"edge"},{"name":"v0.9.0"},{"name":"v0.10.0"},{"name":"v0.11.0-rc.1"}]""", "application/json");
        _httpClient = factory.CreateClient();
    }

    [Fact]
    public async Task Put_OlderBridge_IsToldANewerOneIsOut()
    {
        var connection = await ReadAsync(await PutAsync(NewInstanceId(), Body(agentVersion: "0.9.0")));

        Assert.Equal("0.10.0", connection.LatestAgentVersion);
        Assert.True(connection.UpdateAvailable);
    }

    [Fact]
    public async Task Put_CurrentBridge_IsNotToldToUpdate()
    {
        var connection = await ReadAsync(await PutAsync(NewInstanceId(), Body(agentVersion: "0.10.0")));

        Assert.Equal("0.10.0", connection.LatestAgentVersion);
        Assert.False(connection.UpdateAvailable);
    }

    [Fact]
    public async Task Put_BridgeAheadOfTheLastRelease_IsNotToldToUpdate()
    {
        var connection = await ReadAsync(await PutAsync(NewInstanceId(), Body(agentVersion: "0.11.0")));

        Assert.False(connection.UpdateAvailable);
    }

    [Theory]
    [InlineData("octoprint")]
    [InlineData("prusalink")]
    [InlineData("bambu")]
    [InlineData("sdcp")]
    [InlineData("duet")]
    [InlineData("flashforge")]
    [InlineData("anycubic")]
    public async Task Put_EveryBridgeAdapter_IsToldANewerBridgeIsOut(string kind)
    {
        // Every adapter is printlog-bridge, so they share its releases (printlog-bridge#82).
        var connection = await ReadAsync(await PutAsync(NewInstanceId(), Body(kind: kind, agentVersion: "0.9.0")));

        Assert.Equal("0.10.0", connection.LatestAgentVersion);
        Assert.True(connection.UpdateAvailable);
    }

    [Fact]
    public async Task Put_AgentWithNoKnownReleases_GetsNoLatestVersion()
    {
        var connection = await ReadAsync(await PutAsync(NewInstanceId(), Body(kind: "some-plugin", agentVersion: "0.1.0")));

        Assert.Null(connection.LatestAgentVersion);
        Assert.False(connection.UpdateAvailable);
    }

    [Fact]
    public async Task Put_NoAgentVersion_IsNotToldToUpdate()
    {
        var connection = await ReadAsync(await PutAsync(NewInstanceId(), Body(agentVersion: null)));

        Assert.Equal("0.10.0", connection.LatestAgentVersion);
        Assert.False(connection.UpdateAvailable);
    }

    [Fact]
    public async Task GetAndList_CarryTheNoticeToo()
    {
        var instanceId = NewInstanceId();
        Assert.Equal(HttpStatusCode.Created, (await PutAsync(instanceId, Body(agentVersion: "0.9.0"))).StatusCode);

        var one = await ReadAsync(await SendAsync(HttpMethod.Get, $"/api/Connections/{instanceId}"));
        Assert.True(one.UpdateAvailable);
        Assert.Equal("0.10.0", one.LatestAgentVersion);

        var list = (await (await SendAsync(HttpMethod.Get, "/api/Connections"))
            .Content.ReadFromJsonAsync<List<ConnectionDto>>(TestContext.Current.CancellationToken))!;
        var listed = list.Single(c => c.InstanceId == instanceId);
        Assert.True(listed.UpdateAvailable);
        Assert.Equal("0.10.0", listed.LatestAgentVersion);
    }

    private static string NewInstanceId() => Guid.NewGuid().ToString();

    private static PutConnectionDto Body(string kind = "moonraker", string? agentVersion = "0.1.0") => new()
    {
        Kind = kind,
        DisplayName = "Test bridge",
        AgentVersion = agentVersion,
        PrinterId = IntegrationTestSeeder.TestPrinterId,
    };

    private Task<HttpResponseMessage> PutAsync(string instanceId, PutConnectionDto body)
        => SendAsync(HttpMethod.Put, $"/api/Connections/{Uri.EscapeDataString(instanceId)}", body);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }
        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<ConnectionDto> ReadAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        return (await response.Content.ReadFromJsonAsync<ConnectionDto>(TestContext.Current.CancellationToken))!;
    }
}
