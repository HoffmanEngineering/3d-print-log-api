using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp;

/// <summary>
/// Proves the read/write scope split: a read-only MCP token is forbidden from a write tool,
/// while a token carrying write:printdata can invoke one. Uses the trivial whoami write tool so
/// the policy is exercised independently of any data-mutating tool.
/// </summary>
public class McpWritePolicyTests : IClassFixture<McpDataWebApplicationFactory>
{
    private readonly McpDataWebApplicationFactory _factory;

    public McpWritePolicyTests(McpDataWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ReadOnlyToken_IsForbiddenFromWriteTool()
    {
        await using var client = await _factory.ConnectAsync(
            IntegrationTestSeeder.TestUserOAuthId, new[] { "read:printdata" });

        var code = await McpDataWebApplicationFactory.ToolErrorCode(
            client, "whoami", new Dictionary<string, object?>());

        Assert.Equal("forbidden", code);
    }

    [Fact]
    public async Task WriteToken_CanInvokeWriteTool()
    {
        await using var client = await _factory.ConnectAsync(
            IntegrationTestSeeder.TestUserOAuthId, new[] { "read:printdata", "write:printdata" });

        var result = await client.CallToolAsync("whoami", new Dictionary<string, object?>());

        Assert.True(result.IsError != true);
    }

    /// <summary>
    /// Builds a raw 2026-07-28 handshake-less JSON-RPC tools/call POST: no initialize, no
    /// server/discover, no session. Deliberately bypasses McpClient, which would negotiate for us.
    ///
    /// Every header and _meta key below is REQUIRED by StreamableHttpHandler for the 2026-07-28
    /// path, and omitting any of them gets the request rejected with HTTP 400 *before* tool
    /// dispatch — which would make this test pass for the wrong reason:
    ///   - MCP-Protocol-Version: without it the request is treated as down-level legacy and never
    ///     exercises the new path at all (ValidateProtocolVersionHeader explicitly allows a
    ///     missing header for back-compat).
    ///   - Mcp-Method must be present and match the body's method (ValidateMcpHeaders).
    ///   - Mcp-Name must be present and match params.name, because tools/call has a routing name
    ///     parameter (GetRoutingNameParameter maps tools/call -> "name").
    ///   - _meta io.modelcontextprotocol/protocolVersion must be present and match the header
    ///     (ValidateProtocolVersionEnvelope).
    ///   - _meta io.modelcontextprotocol/clientCapabilities must be a JSON object
    ///     (ValidateRequiredPerRequestMeta).
    /// </summary>
    private static HttpRequestMessage RawHandshakeLessToolCall(string tool, string token)
    {
        const string ProtocolVersion = "2026-07-28";

        var body =
            $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{{" +
            $"\"name\":\"{tool}\",\"arguments\":{{}}," +
            $"\"_meta\":{{" +
            $"\"io.modelcontextprotocol/protocolVersion\":\"{ProtocolVersion}\"," +
            $"\"io.modelcontextprotocol/clientCapabilities\":{{}}" +
            $"}}}}}}";

        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", ProtocolVersion);
        request.Headers.Add("Mcp-Method", "tools/call");
        request.Headers.Add("Mcp-Name", tool);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>
    /// Unwraps a JSON-RPC payload from a response body that may be either raw JSON or a
    /// Streamable HTTP SSE stream. We must advertise text/event-stream in Accept (the transport
    /// requires it), so the server is free to answer either way and does in fact answer with SSE.
    /// Rather than pin that choice — an SDK-owned wire detail — accept both shapes: concatenate
    /// the "data:" lines of an event stream, or pass raw JSON through untouched.
    /// </summary>
    private static string UnwrapJsonRpcPayload(string body)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var data = string.Concat(body
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
            .Select(line => line.Substring("data:".Length).Trim()));

        Assert.False(
            string.IsNullOrEmpty(data),
            $"Response body was neither JSON nor an SSE stream carrying data: {body}");

        return data;
    }

    /// <summary>
    /// The security-relevant boundary of the v2 upgrade. A read-only token is admitted to /mcp by
    /// the endpoint policy (it carries one data scope), so the write-tool denial rests entirely on
    /// the SDK's per-tool-class authorization filter. v2 can reach tool dispatch without a
    /// handshake; this proves the filter still runs on that path.
    /// </summary>
    [Fact]
    public async Task ReadOnlyToken_HandshakeLessWriteToolCall_DoesNotSucceed()
    {
        var token = TestJwt.Create(
            TestJwt.McpAudience,
            subject: IntegrationTestSeeder.TestUserOAuthId,
            scopes: new[] { "read:printdata" });

        var response = await _factory.CreateClient()
            .SendAsync(RawHandshakeLessToolCall("whoami", token));
        var body = await response.Content.ReadAsStringAsync();

        // The request MUST be admitted by the endpoint (HTTP 200) so that the refusal we go on to
        // assert can only have come from the per-tool-class authorization filter. Treating a
        // 400/401/403 as "refused" would let this test keep passing while silently no longer
        // covering the tool layer at all — e.g. if the "Mcp" endpoint policy were tightened to
        // require write:printdata, every read-only call would be rejected at the endpoint and this
        // test would go green without the filter ever running. A failure here is therefore not
        // necessarily a vulnerability; it means the authorization topology moved and this test
        // needs to be re-pointed at whatever layer now owns the boundary.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The refusal must be in the payload — either a JSON-RPC error or an isError result.
        // Which of the two is an SDK-owned wire detail this test does not pin. Note we cannot
        // assert on the *absence* of a success payload:
        // whoami returns a bare long, so a successful body is just the user id as text and
        // there is no distinctive property name to look for. Requiring a positive error
        // signal is what keeps this assertion from being vacuous.
        using var doc = JsonDocument.Parse(UnwrapJsonRpcPayload(body));
        var isJsonRpcError = doc.RootElement.TryGetProperty("error", out _);
        var isToolError = doc.RootElement.TryGetProperty("result", out var result)
            && result.TryGetProperty("isError", out var flag)
            && flag.GetBoolean();

        Assert.True(
            isJsonRpcError || isToolError,
            $"Handshake-less write-tool call was neither refused nor an error result: {body}");
    }
}
