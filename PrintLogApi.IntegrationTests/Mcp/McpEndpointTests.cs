using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    /// <summary>
    /// Drives the /mcp Streamable HTTP endpoint end to end: tools/list and tools/call via the SDK
    /// client (with a locally signed MCP token), plus raw-HTTP assertions on auth, OAuth
    /// protected-resource metadata, and the unauthenticated challenge.
    /// </summary>
    public class McpEndpointTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly CustomWebApplicationFactory _factory;

        public McpEndpointTests(CustomWebApplicationFactory factory) => _factory = factory;

        private static string McpToken(string subject = "auth0|mcp-endpoint-user", bool withScope = true) =>
            TestJwt.Create(TestJwt.McpAudience, subject: subject,
                scopes: withScope ? new[] { "read:printdata" } : null);

        private async Task<McpClient> ConnectAsync(string token)
        {
            var httpClient = _factory.CreateClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                },
                httpClient);
            return await McpClient.CreateAsync(transport);
        }

        private HttpRequestMessage RpcPost(string method, string token)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent(
                    $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"{method}\"}}",
                    Encoding.UTF8,
                    "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            if (token != null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            return request;
        }

        [Fact]
        public async Task ListTools_ReturnsPing()
        {
            await using var client = await ConnectAsync(McpToken());
            var tools = await client.ListToolsAsync();
            Assert.Contains(tools, t => t.Name == "ping");
        }

        /// <summary>
        /// Pins the exposed surface. A tool registers itself just by carrying [McpServerTool], so
        /// without this a new one ships unreviewed and a deleted one goes unnoticed. Registration is
        /// also resolved at activation rather than compile time, so a tool whose service is not
        /// registered only fails here.
        /// </summary>
        [Fact]
        public async Task ListTools_ExposesExactlyTheV1Surface()
        {
            await using var client = await ConnectAsync(McpToken());
            var tools = await client.ListToolsAsync();

            // A read-only token sees ONLY the read surface: the SDK's authorization filter hides the
            // write tools (which require write:printdata) from tools/list. See McpWriteSurfaceTests
            // for the write-token view.
            Assert.Equal(
                new[]
                {
                    "find_material",
                    "get_material_inventory",
                    "get_print",
                    "get_print_summary",
                    "get_printer",
                    "get_printer_stats",
                    "list_printers",
                    "list_projects",
                    "ping",
                    "search_prints",
                },
                tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }

        [Fact]
        public async Task CallPing_Echoes()
        {
            await using var client = await ConnectAsync(McpToken());
            var result = await client.CallToolAsync(
                "ping",
                new Dictionary<string, object> { ["message"] = "hi" });
            var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
            Assert.Equal("pong: hi", text);
        }

        [Fact]
        public async Task NoToken_Is401()
        {
            var resp = await _factory.CreateClient().SendAsync(RpcPost("tools/list", token: null));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        [Fact]
        public async Task NoScope_Is403()
        {
            var resp = await _factory.CreateClient().SendAsync(RpcPost("tools/list", McpToken(withScope: false)));
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }

        [Fact]
        public async Task MissingMappedUser_CannotListTools()
        {
            var resp = await _factory.CreateClient().SendAsync(RpcPost("tools/list", McpToken(subject: null)));
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }

        [Fact]
        public async Task Challenge_ReferencesResourceMetadata()
        {
            var resp = await _factory.CreateClient().SendAsync(RpcPost("tools/list", token: null));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);

            var header = resp.Headers.WwwAuthenticate.ToString();
            Assert.Contains("resource_metadata", header);
        }

        [Fact]
        public async Task Metadata_AdvertisesResourceAuth0AndScope()
        {
            var client = _factory.CreateClient();
            var challenge = await client.SendAsync(RpcPost("tools/list", token: null));
            var header = challenge.Headers.WwwAuthenticate.ToString();
            var match = Regex.Match(header, "resource_metadata=\"([^\"]+)\"");
            Assert.True(match.Success, $"No resource_metadata in challenge: {header}");

            var metadataResp = await client.GetAsync(match.Groups[1].Value);
            Assert.Equal(HttpStatusCode.OK, metadataResp.StatusCode);

            using var doc = JsonDocument.Parse(await metadataResp.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            Assert.Equal(TestJwt.McpAudience, root.GetProperty("resource").GetString());

            var authServers = root.GetProperty("authorization_servers").EnumerateArray()
                .Select(e => e.GetString()).ToList();
            Assert.Contains(TestJwt.Issuer, authServers);

            var scopes = root.GetProperty("scopes_supported").EnumerateArray()
                .Select(e => e.GetString()).ToList();
            Assert.Contains("read:printdata", scopes);
        }
    }
}
