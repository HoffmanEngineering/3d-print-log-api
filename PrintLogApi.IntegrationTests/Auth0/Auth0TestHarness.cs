using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using PrintLogApi.Services;

namespace PrintLogApi.IntegrationTests.Auth0
{
    /// <summary>Records requests and returns caller-supplied responses for the Auth0 Management API.</summary>
    public sealed class StubHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        // Always assigned by the test before the harness handles a request.
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = null!;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Responder(request));
        }
    }

    public sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    public static class Auth0TestHarness
    {
        public const string McpAudience = "https://test.mcp";
        public const string WebAudience = "https://test.api";

        public static Auth0Service CreateService(StubHandler handler)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Auth0Management:Domain"] = "test.auth0.com",
                    ["Auth0Management:ClientId"] = "mgmt-client",
                    ["Auth0Management:ClientSecret"] = "mgmt-secret",
                    ["Auth0:McpIdentifier"] = McpAudience,
                })
                .Build();

            var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 });
            return new Auth0Service(new StubHttpClientFactory(handler), config, cache);
        }

        public static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body) };

        public static HttpResponseMessage TokenResponse() =>
            Json("{\"access_token\":\"mgmt-token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}");

        public static string Grant(
            string id, string audience, string clientId = "client-1",
            string scope = "read:printdata", string userId = "auth0|user") =>
            $"{{\"id\":\"{id}\",\"clientID\":\"{clientId}\",\"audience\":\"{audience}\"," +
            $"\"scope\":[{ScopeArray(scope)}],\"user_id\":\"{userId}\"}}";

        private static string ScopeArray(string scope) =>
            string.IsNullOrEmpty(scope) ? "" : $"\"{scope}\"";

        public static string GrantsPage(int start, int total, params string[] grants) =>
            $"{{\"grants\":[{string.Join(",", grants)}],\"start\":{start},\"limit\":100," +
            $"\"length\":{grants.Length},\"total\":{total}}}";
    }
}
