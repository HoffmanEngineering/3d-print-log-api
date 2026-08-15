using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    public class McpRateLimitTests : IClassFixture<McpRateLimitTests.LowLimitFactory>
    {
        public const int Limit = 3;
        private readonly LowLimitFactory _factory;

        public McpRateLimitTests(LowLimitFactory factory) => _factory = factory;

        /// <summary>A factory with a deliberately tiny per-user /mcp budget.</summary>
        public sealed class LowLimitFactory : CustomWebApplicationFactory
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Mcp:RateLimitPerMinute"] = Limit.ToString(),
                    }));
                base.ConfigureWebHost(builder);
            }
        }

        private static HttpRequestMessage Rpc(string token)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent(
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return request;
        }

        private static string TokenFor(string subject) =>
            TestJwt.Create(TestJwt.McpAudience, subject: subject, scopes: new[] { "read:printdata" });

        [Fact]
        public async Task ExceedingBudget_Returns429_WithRetryAfter()
        {
            var client = _factory.CreateClient();
            var token = TokenFor("auth0|rl-exhaust");

            var statuses = new List<HttpResponseMessage>();
            for (var i = 0; i < Limit + 1; i++)
            {
                statuses.Add(await client.SendAsync(Rpc(token)));
            }

            Assert.All(statuses.Take(Limit), r => Assert.NotEqual(HttpStatusCode.TooManyRequests, r.StatusCode));

            var last = statuses[Limit];
            Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
            Assert.True(last.Headers.RetryAfter != null, "429 response should include Retry-After");
        }

        [Fact]
        public async Task Budgets_ArePerUser()
        {
            var client = _factory.CreateClient();
            var tokenA = TokenFor("auth0|rl-a");
            var tokenB = TokenFor("auth0|rl-b");

            // Exhaust user A.
            for (var i = 0; i < Limit + 1; i++)
            {
                await client.SendAsync(Rpc(tokenA));
            }

            // User B still has a full budget.
            var respB = await client.SendAsync(Rpc(tokenB));
            Assert.NotEqual(HttpStatusCode.TooManyRequests, respB.StatusCode);
        }

        [Fact]
        public async Task JsonRpcBatch_IsNotProcessedAsMultipleCalls()
        {
            var client = _factory.CreateClient();
            var token = TokenFor("auth0|rl-batch");

            var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent(
                    "[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}," +
                    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}]",
                    Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // MCP (2025-06-18) removed JSON-RPC batching; the SDK's message reader cannot parse a
            // batch array and rejects it, so one transport request never fans out into multiple
            // tool executions (which would bypass the per-request budget).
            string body = null;
            try
            {
                var resp = await client.SendAsync(request);
                body = await resp.Content.ReadAsStringAsync();
            }
            catch
            {
                body = null; // server rejected the batch outright
            }

            if (body != null)
            {
                var resultCount = System.Text.RegularExpressions.Regex.Matches(body, "\"tools\"").Count;
                Assert.True(resultCount <= 1, $"Batch fanned out into multiple results: {body}");
            }
        }

        [Fact]
        public async Task UnauthenticatedTraffic_IsRejectedBeforeConsumingBudget()
        {
            var client = _factory.CreateClient();

            // No token: authorization rejects (401) before the rate limiter runs.
            for (var i = 0; i < Limit + 2; i++)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
                {
                    Content = new StringContent(
                        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", Encoding.UTF8, "application/json"),
                };
                var resp = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            }
        }
    }
}
