using System.Net;
using System.Text;
using Microsoft.ApplicationInsights.DataContracts;
using ModelContextProtocol;
using PrintLogApi.Mcp.Docs;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp.Docs;

/// <summary>
/// A client that offers protocol version 2026-07-28 through <c>initialize</c> gets a JSON-RPC
/// error (-32022) in a 200 response, and the SDK logs an
/// <see cref="UnsupportedProtocolVersionException"/> at Warning from
/// <c>ModelContextProtocol.Server.McpServer</c>. That is the client's mistake (the SDK's own
/// client does it when its <c>server/discover</c> probe times out and it falls back to
/// <c>initialize</c>), so it must not reach Application Insights as an exception. Production
/// recorded 26 of them in 30 days on /mcp/docs.
/// </summary>
public class McpProtocolVersionTelemetryTests : IClassFixture<McpDataWebApplicationFactory>
{
    private const string UnavailableVersion = "2026-07-28";

    private readonly McpDataWebApplicationFactory _factory;

    public McpProtocolVersionTelemetryTests(McpDataWebApplicationFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// An <c>initialize</c> naming the version in its body only. With the MCP-Protocol-Version
    /// header as well, the transport answers 400 before the handler runs and nothing is logged.
    /// </summary>
    private static HttpRequestMessage Initialize(string protocolVersion)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, McpDocsEndpoint.Path)
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{" +
                $"\"protocolVersion\":\"{protocolVersion}\",\"capabilities\":{{}}," +
                "\"clientInfo\":{\"name\":\"telemetry-test\",\"version\":\"1.0\"}}}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return request;
    }

    [Fact]
    public async Task Initialize_with_an_unavailable_version_is_not_recorded_as_an_exception()
    {
        _factory.Telemetry.Clear();

        var response = await _factory.CreateClient().SendAsync(Initialize(UnavailableVersion), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("-32022", body);
        Assert.DoesNotContain(
            _factory.Telemetry.Items.OfType<ExceptionTelemetry>(),
            e => e.Exception is UnsupportedProtocolVersionException);
    }
}
