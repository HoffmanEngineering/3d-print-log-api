using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PrintLogApi.IntegrationTests.Mcp
{
    /// <summary>
    /// A <see cref="CustomWebApplicationFactory"/> that also seeds <see cref="McpTestData"/> and
    /// provides helpers for connecting an MCP client and calling tools as a given user.
    /// </summary>
    public class McpDataWebApplicationFactory : CustomWebApplicationFactory
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);
            using var scope = host.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            McpTestData.Seed(context);
            return host;
        }

        /// <summary>Connects an MCP client authenticated as the given Auth0 subject (default: primary seeded user).</summary>
        public async Task<McpClient> ConnectAsync(string subject = IntegrationTestSeeder.TestUserOAuthId)
        {
            return await ConnectAsync(subject, new[] { "read:printdata" });
        }

        /// <summary>Connects an MCP client with an explicit scope set (e.g. read + write for write tools).</summary>
        public async Task<McpClient> ConnectAsync(string subject, string[] scopes)
        {
            var token = TestJwt.Create(TestJwt.McpAudience, subject: subject, scopes: scopes);
            var httpClient = CreateClient();
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

        /// <summary>Returns true if the tool call surfaced an error (via IsError result or McpException).</summary>
        public static async Task<bool> IsToolError(McpClient client, string tool, Dictionary<string, object> arguments)
        {
            try
            {
                var result = await client.CallToolAsync(tool, arguments);
                return result.IsError == true;
            }
            catch (McpException)
            {
                return true;
            }
        }

        /// <summary>
        /// Returns the tool error code ("forbidden"/"not_found"/"error") or null when the call
        /// succeeded. Authorization failures surface as an <see cref="McpException"/> ("forbidden").
        /// </summary>
        public static async Task<string> ToolErrorCode(McpClient client, string tool, Dictionary<string, object> arguments)
        {
            try
            {
                var result = await client.CallToolAsync(tool, arguments);
                if (result.IsError != true)
                {
                    return null;
                }

                var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "";
                if (text.Contains("not found", StringComparison.OrdinalIgnoreCase))
                {
                    return "not_found";
                }
                if (text.Contains("denied", StringComparison.OrdinalIgnoreCase))
                {
                    return "forbidden";
                }
                return "error";
            }
            catch (McpException)
            {
                return "forbidden";
            }
        }
    }
}
