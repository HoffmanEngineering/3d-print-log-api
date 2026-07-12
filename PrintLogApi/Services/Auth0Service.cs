using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using PrintLogApi.Exceptions;
using PrintLogApi.Models.DTOs;

namespace PrintLogApi.Services
{
    public class Auth0Service : IAuth0Service
    {
        private const string TokenCacheKey = "auth0:management-token";
        private const string ReadPrintDataScope = "read:printdata";
        private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(30);
        private static readonly SemaphoreSlim TokenLock = new(1, 1);

        private readonly IHttpClientFactory _clientFactory;
        private readonly IConfiguration _configuration;
        private readonly IMemoryCache _cache;

        public Auth0Service(IHttpClientFactory clientFactory, IConfiguration configuration, IMemoryCache cache)
        {
            _clientFactory = clientFactory;
            _configuration = configuration;
            _cache = cache;
        }

        private string ManagementBaseUrl => $"https://{_configuration["Auth0Management:Domain"]}/api/v2";

        /// <summary>
        ///   Gets an access token needed to interact with the Auth0 Management Apis
        /// </summary>
        public async Task<string> GetManagementApiBearerToken()
        {
            var token = await GetCachedAccessTokenAsync(CancellationToken.None);
            return $"Bearer {token}";
        }

        private async Task<string> GetCachedAccessTokenAsync(CancellationToken ct)
        {
            if (_cache.TryGetValue(TokenCacheKey, out string cached) && !string.IsNullOrEmpty(cached))
            {
                return cached;
            }

            await TokenLock.WaitAsync(ct);
            try
            {
                if (_cache.TryGetValue(TokenCacheKey, out cached) && !string.IsNullOrEmpty(cached))
                {
                    return cached;
                }

                var jwt = await FetchAccessTokenAsync(ct);
                // Refresh a minute before actual expiry to avoid using an about-to-expire token.
                var lifetime = TimeSpan.FromSeconds(Math.Max(30, jwt.ExpiresIn - 60));
                _cache.Set(TokenCacheKey, jwt.AccessToken, new MemoryCacheEntryOptions()
                    .SetSize(1)
                    .SetAbsoluteExpiration(lifetime));
                return jwt.AccessToken;
            }
            finally
            {
                TokenLock.Release();
            }
        }

        private async Task<Jwt> FetchAccessTokenAsync(CancellationToken ct)
        {
            var tokenUrl = $"https://{_configuration["Auth0Management:Domain"]}/oauth/token";
            var content = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "client_credentials"),
                new("client_id", _configuration["Auth0Management:ClientId"]),
                new("client_secret", _configuration["Auth0Management:ClientSecret"]),
                new("audience", $"https://{_configuration["Auth0Management:Domain"]}/api/v2/"),
            };

            using var client = CreateClient();
            using var body = new FormUrlEncodedContent(content);
            using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl) { Content = body };

            var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new Auth0ApiException($"Auth0 token request failed with status {(int)response.StatusCode}.");
            }

            var result = await response.Content.ReadAsStringAsync(ct);
            var jwt = JsonSerializer.Deserialize<Jwt>(result);
            if (jwt is null || string.IsNullOrEmpty(jwt.AccessToken))
            {
                throw new Auth0ApiException("Auth0 token response did not contain an access token.");
            }
            return jwt;
        }

        public async Task<IReadOnlyList<ConnectedAgentDto>> ListMcpGrants(string authUserId, CancellationToken ct)
        {
            var mcpAudience = _configuration["Auth0:McpIdentifier"];
            var agents = new List<ConnectedAgentDto>();

            var page = 0;
            while (true)
            {
                var url = $"{ManagementBaseUrl}/grants" +
                    $"?user_id={Uri.EscapeDataString(authUserId)}" +
                    $"&audience={Uri.EscapeDataString(mcpAudience)}" +
                    $"&per_page=100&page={page}&include_totals=true";

                var json = await SendManagementAsync(HttpMethod.Get, url, ct);
                var pageResult = JsonSerializer.Deserialize<GrantsPage>(json)
                    ?? new GrantsPage { Grants = new List<Auth0Grant>() };
                var grants = pageResult.Grants ?? new List<Auth0Grant>();

                foreach (var grant in grants)
                {
                    // Defense-in-depth: re-check audience and scope client-side.
                    if (grant.Audience == mcpAudience
                        && grant.Scope != null
                        && grant.Scope.Contains(ReadPrintDataScope))
                    {
                        agents.Add(new ConnectedAgentDto(grant.Id, grant.ClientId, grant.Scope));
                    }
                }

                var fetched = pageResult.Start + grants.Count;
                if (grants.Count == 0 || fetched >= pageResult.Total)
                {
                    break;
                }
                page++;
            }

            return agents;
        }

        public async Task RevokeMcpGrant(string authUserId, string grantId, CancellationToken ct)
        {
            // Re-list the already user+audience+scope-filtered grants and confirm ownership before
            // deleting. Never delete a grant merely because its id was supplied by the caller.
            var owned = await ListMcpGrants(authUserId, ct);
            if (owned.All(g => g.GrantId != grantId))
            {
                throw new NotFoundException($"MCP grant '{grantId}' was not found for this user.");
            }

            var url = $"{ManagementBaseUrl}/grants/{Uri.EscapeDataString(grantId)}";
            await SendManagementAsync(HttpMethod.Delete, url, ct, treat404AsSuccess: true);
        }

        /// <summary>
        /// Sends an authenticated Management API request, retrying once on 401 after evicting the
        /// cached token. Returns the response body; maps non-success (except an optional 404) to a
        /// secret-free <see cref="Auth0ApiException"/>.
        /// </summary>
        private async Task<string> SendManagementAsync(
            HttpMethod method, string url, CancellationToken ct, bool treat404AsSuccess = false)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var token = await GetCachedAccessTokenAsync(ct);
                using var client = CreateClient();
                using var request = new HttpRequestMessage(method, url);
                request.Headers.Add("Authorization", $"Bearer {token}");

                var response = await client.SendAsync(request, ct);

                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    _cache.Remove(TokenCacheKey); // stale token; refresh and retry once
                    continue;
                }

                if (treat404AsSuccess && response.StatusCode == HttpStatusCode.NotFound)
                {
                    return string.Empty;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new Auth0ApiException(
                        $"Auth0 Management API request failed with status {(int)response.StatusCode}.");
                }

                return await response.Content.ReadAsStringAsync(ct);
            }

            throw new Auth0ApiException("Auth0 Management API request failed after retrying authentication.");
        }

        private HttpClient CreateClient()
        {
            var client = _clientFactory.CreateClient();
            client.Timeout = HttpTimeout;
            return client;
        }

        /// <summary>
        ///     Gets a user's information from Auth0
        /// </summary>
        public async Task GetUser(string oauthUserId)
        {
            var requestUrl = $"{ManagementBaseUrl}/users/{Uri.EscapeDataString(oauthUserId)}";
            await SendManagementAsync(HttpMethod.Get, requestUrl, CancellationToken.None);
        }

        /// <summary>
        ///   Permanently delete a user from Auth0 by their oauth id.
        /// </summary>
        public async Task DeleteUser(string oauthUserId)
        {
            var requestUrl = $"{ManagementBaseUrl}/users/{Uri.EscapeDataString(oauthUserId)}";
            await SendManagementAsync(HttpMethod.Delete, requestUrl, CancellationToken.None);
        }

        private sealed class GrantsPage
        {
            [JsonPropertyName("grants")] public List<Auth0Grant> Grants { get; set; }
            [JsonPropertyName("start")] public int Start { get; set; }
            [JsonPropertyName("limit")] public int Limit { get; set; }
            [JsonPropertyName("length")] public int Length { get; set; }
            [JsonPropertyName("total")] public int Total { get; set; }
        }

        private sealed class Auth0Grant
        {
            [JsonPropertyName("id")] public string Id { get; set; }
            [JsonPropertyName("clientID")] public string ClientId { get; set; }
            [JsonPropertyName("audience")] public string Audience { get; set; }
            [JsonPropertyName("scope")] public List<string> Scope { get; set; }
            [JsonPropertyName("user_id")] public string UserId { get; set; }
        }
    }
}
