using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PrintLogApi.Exceptions;
using Xunit;

namespace PrintLogApi.IntegrationTests.Auth0
{
    public class RevokeMcpGrantTests
    {
        private static StubHandler HandlerReturning(string grantsBody)
        {
            return new StubHandler
            {
                Responder = request =>
                {
                    if (request.RequestUri!.AbsolutePath.EndsWith("/oauth/token"))
                    {
                        return Auth0TestHarness.TokenResponse();
                    }
                    if (request.Method == HttpMethod.Delete)
                    {
                        return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
                    }
                    return Auth0TestHarness.Json(grantsBody);
                },
            };
        }

        private static int DeleteCount(StubHandler handler) =>
            handler.Requests.Count(r => r.Method == HttpMethod.Delete);

        [Fact]
        public async Task RevokesOwnedMcpGrant()
        {
            var handler = HandlerReturning(Auth0TestHarness.GrantsPage(0, 1,
                Auth0TestHarness.Grant("grant-x", Auth0TestHarness.McpAudience)));
            var service = Auth0TestHarness.CreateService(handler);

            await service.RevokeMcpGrant("auth0|user", "grant-x", CancellationToken.None);

            Assert.Equal(1, DeleteCount(handler));
            var delete = handler.Requests.First(r => r.Method == HttpMethod.Delete);
            Assert.EndsWith("/grants/grant-x", delete.RequestUri!.AbsolutePath);
        }

        [Fact]
        public async Task RefusesDifferentAudienceGrant()
        {
            // The caller owns this grant, but it targets the web app audience — not MCP.
            var handler = HandlerReturning(Auth0TestHarness.GrantsPage(0, 1,
                Auth0TestHarness.Grant("web-grant", Auth0TestHarness.WebAudience)));
            var service = Auth0TestHarness.CreateService(handler);

            await Assert.ThrowsAsync<NotFoundException>(() =>
                service.RevokeMcpGrant("auth0|user", "web-grant", CancellationToken.None));
            Assert.Equal(0, DeleteCount(handler));
        }

        [Fact]
        public async Task RefusesGrantNotBelongingToCaller()
        {
            // The caller's MCP grants do not include the requested id.
            var handler = HandlerReturning(Auth0TestHarness.GrantsPage(0, 1,
                Auth0TestHarness.Grant("grant-x", Auth0TestHarness.McpAudience)));
            var service = Auth0TestHarness.CreateService(handler);

            await Assert.ThrowsAsync<NotFoundException>(() =>
                service.RevokeMcpGrant("auth0|user", "someone-elses-grant", CancellationToken.None));
            Assert.Equal(0, DeleteCount(handler));
        }
    }
}
