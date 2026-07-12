using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    /// <summary>
    /// Verifies the dedicated MCP resource boundary: the McpAccess policy accepts only
    /// MCP-audience tokens carrying read:printdata and a mapped user, and never leaks
    /// across to (or from) the normal app-audience bearer scheme.
    /// </summary>
    public class McpAuthTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly CustomWebApplicationFactory _factory;

        public McpAuthTests(CustomWebApplicationFactory factory) => _factory = factory;

        private static HttpRequestMessage Probe(string path, string token = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (token != null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            return request;
        }

        [Fact]
        public async Task McpAccessPolicy_IsRegistered_AndGrantsValidMcpToken()
        {
            var token = TestJwt.Create(TestJwt.McpAudience, scopes: new[] { "read:printdata" });
            var resp = await _factory.CreateClient().SendAsync(Probe("/api/mcp-auth-probe", token));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        [Fact]
        public async Task McpAccess_RejectsWebAudienceToken()
        {
            var token = TestJwt.Create(TestJwt.ApiAudience, scopes: new[] { "read:printdata" });
            var resp = await _factory.CreateClient().SendAsync(Probe("/api/mcp-auth-probe", token));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        [Fact]
        public async Task NormalApiEndpoint_RejectsMcpAudienceToken()
        {
            var token = TestJwt.Create(TestJwt.McpAudience, scopes: new[] { "read:printdata" });
            var resp = await _factory.CreateClient().SendAsync(Probe("/api/web-auth-probe", token));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        [Fact]
        public async Task NormalApiEndpoint_AcceptsWebAudienceToken()
        {
            var token = TestJwt.Create(TestJwt.ApiAudience);
            var resp = await _factory.CreateClient().SendAsync(Probe("/api/web-auth-probe", token));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        [Fact]
        public async Task McpAccess_MissingScope_Is403()
        {
            var token = TestJwt.Create(TestJwt.McpAudience);
            var resp = await _factory.CreateClient().SendAsync(Probe("/api/mcp-auth-probe", token));
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }

        [Fact]
        public async Task McpAccess_MissingSubject_Is403()
        {
            var token = TestJwt.Create(TestJwt.McpAudience, subject: null, scopes: new[] { "read:printdata" });
            var resp = await _factory.CreateClient().SendAsync(Probe("/api/mcp-auth-probe", token));
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }

        [Fact]
        public async Task McpAccess_NoToken_Is401()
        {
            var resp = await _factory.CreateClient().SendAsync(Probe("/api/mcp-auth-probe"));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        [Fact]
        public async Task McpAccess_ExpiredToken_Is401()
        {
            var token = TestJwt.Create(
                TestJwt.McpAudience,
                scopes: new[] { "read:printdata" },
                notBefore: DateTime.UtcNow.AddHours(-2),
                expires: DateTime.UtcNow.AddHours(-1));
            var resp = await _factory.CreateClient().SendAsync(Probe("/api/mcp-auth-probe", token));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        [Fact]
        public async Task McpAccess_WrongIssuer_Is401()
        {
            var token = TestJwt.Create(
                TestJwt.McpAudience,
                scopes: new[] { "read:printdata" },
                issuer: "https://evil-issuer/");
            var resp = await _factory.CreateClient().SendAsync(Probe("/api/mcp-auth-probe", token));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
    }
}
