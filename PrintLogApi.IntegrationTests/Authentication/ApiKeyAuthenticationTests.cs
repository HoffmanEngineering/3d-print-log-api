using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using PrintLogApi.Models.DTOs.UserApiKeys;
using Xunit;

namespace PrintLogApi.IntegrationTests.Authentication
{
    public class ApiKeyAuthenticationTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly HttpClient _httpClient;
        private readonly CustomWebApplicationFactory _factory;
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        public ApiKeyAuthenticationTests(CustomWebApplicationFactory factory)
        {
            _factory = factory;
            _httpClient = factory.CreateClient();
        }

        private async Task<NewUserApiKeyDto> GenerateApiKey(string description = "Test Key")
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/UserApiKeys");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            request.Content = JsonContent.Create(new AddNewApiKeyDto { Description = description });
            var response = await _httpClient.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<NewUserApiKeyDto>(JsonOptions);
        }

        [Fact]
        public async Task ApiKey_ValidKey_AuthenticatesRequest()
        {
            var key = await GenerateApiKey("Auth Test Key");

            var request = new HttpRequestMessage(HttpMethod.Get, "/api/UserApiKeys");
            request.Headers.Add("X-Api-Key", key.PublicKey);

            var response = await _httpClient.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task ApiKey_InvalidKey_ReturnsUnauthorized()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/UserApiKeys");
            request.Headers.Add("X-Api-Key", "INVALIDKEY00000000000000000000000");

            var response = await _httpClient.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task ApiKey_AfterDeactivation_ReturnsUnauthorized()
        {
            // Generate a key and use it once to warm the cache
            var key = await GenerateApiKey("Deactivation Cache Test");

            var warmupRequest = new HttpRequestMessage(HttpMethod.Get, "/api/UserApiKeys");
            warmupRequest.Headers.Add("X-Api-Key", key.PublicKey);
            var warmupResponse = await _httpClient.SendAsync(warmupRequest);
            Assert.Equal(HttpStatusCode.OK, warmupResponse.StatusCode);

            // Deactivate the key
            var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/UserApiKeys/{key.Id}");
            deleteRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            var deleteResponse = await _httpClient.SendAsync(deleteRequest);
            Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

            // The deactivated key must not authenticate even though it was recently cached
            var secondRequest = new HttpRequestMessage(HttpMethod.Get, "/api/UserApiKeys");
            secondRequest.Headers.Add("X-Api-Key", key.PublicKey);
            var secondResponse = await _httpClient.SendAsync(secondRequest);

            Assert.Equal(HttpStatusCode.Unauthorized, secondResponse.StatusCode);
        }
    }
}
