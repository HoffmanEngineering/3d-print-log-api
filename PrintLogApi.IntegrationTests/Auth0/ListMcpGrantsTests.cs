using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PrintLogApi.IntegrationTests.Auth0
{
    public class ListMcpGrantsTests
    {
        private static HttpResponseMessage RouteDefault(HttpRequestMessage request, string grantsBody)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth/token"))
            {
                return Auth0TestHarness.TokenResponse();
            }
            return Auth0TestHarness.Json(grantsBody);
        }

        [Fact]
        public async Task ReturnsOnly_McpAudience_WithReadPrintData()
        {
            var handler = new StubHandler
            {
                Responder = request => RouteDefault(request, Auth0TestHarness.GrantsPage(
                    start: 0, total: 3,
                    Auth0TestHarness.Grant("mcp-ok", Auth0TestHarness.McpAudience, scope: "read:printdata"),
                    Auth0TestHarness.Grant("web-grant", Auth0TestHarness.WebAudience, scope: "read:printdata"),
                    Auth0TestHarness.Grant("mcp-wrong-scope", Auth0TestHarness.McpAudience, scope: "openid"))),
            };
            var service = Auth0TestHarness.CreateService(handler);

            var agents = await service.ListMcpGrants("auth0|user", CancellationToken.None);

            Assert.Single(agents);
            Assert.Equal("mcp-ok", agents[0].GrantId);
        }

        [Fact]
        public async Task PagesThroughAllResults()
        {
            var page = 0;
            var handler = new StubHandler();
            handler.Responder = request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/oauth/token"))
                {
                    return Auth0TestHarness.TokenResponse();
                }

                // First call returns 100 grants (total 150); second returns the remaining 50.
                if (page++ == 0)
                {
                    var first = Enumerable.Range(0, 100)
                        .Select(i => Auth0TestHarness.Grant($"g{i}", Auth0TestHarness.McpAudience)).ToArray();
                    return Auth0TestHarness.Json(Auth0TestHarness.GrantsPage(0, 150, first));
                }

                var second = Enumerable.Range(100, 50)
                    .Select(i => Auth0TestHarness.Grant($"g{i}", Auth0TestHarness.McpAudience)).ToArray();
                return Auth0TestHarness.Json(Auth0TestHarness.GrantsPage(100, 150, second));
            };
            var service = Auth0TestHarness.CreateService(handler);

            var agents = await service.ListMcpGrants("auth0|user", CancellationToken.None);

            Assert.Equal(150, agents.Count);
            var grantRequests = handler.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/grants"));
            Assert.Equal(2, grantRequests);
        }

        [Fact]
        public async Task ExcludesGrantsBelongingToAnotherSubject()
        {
            // Defense-in-depth: even if Auth0's user_id filter misbehaves and returns a foreign
            // subject's grant, the client-side check must drop it.
            var handler = new StubHandler
            {
                Responder = request => RouteDefault(request, Auth0TestHarness.GrantsPage(
                    start: 0, total: 2,
                    Auth0TestHarness.Grant("mine", Auth0TestHarness.McpAudience, userId: "auth0|user"),
                    Auth0TestHarness.Grant("theirs", Auth0TestHarness.McpAudience, userId: "auth0|someone-else"))),
            };
            var service = Auth0TestHarness.CreateService(handler);

            var agents = await service.ListMcpGrants("auth0|user", CancellationToken.None);

            Assert.Single(agents);
            Assert.Equal("mine", agents[0].GrantId);
        }

        [Fact]
        public async Task EncodesUserIdInQuery()
        {
            var handler = new StubHandler
            {
                Responder = request => RouteDefault(request, Auth0TestHarness.GrantsPage(0, 0)),
            };
            var service = Auth0TestHarness.CreateService(handler);

            await service.ListMcpGrants("auth0|abc123", CancellationToken.None);

            var grantRequest = handler.Requests.First(r => r.RequestUri!.AbsolutePath.EndsWith("/grants"));
            Assert.Contains("user_id=auth0%7Cabc123", grantRequest.RequestUri!.Query);
        }
    }
}
