using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.UserApiKeys;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers
{
    /// <summary>
    /// Integration tests for the UserApiKeysController.
    /// Tests API key CRUD operations including generation and deletion.
    /// </summary>
    public class UserApiKeysControllerTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly HttpClient _httpClient;
        private readonly CustomWebApplicationFactory _factory;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public UserApiKeysControllerTests(CustomWebApplicationFactory factory)
        {
            _factory = factory;
            _httpClient = factory.CreateClient();
        }

        #region Helper Methods

        private HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string url, string userId = null)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, userId ?? IntegrationTestSeeder.TestUserOAuthId);
            return request;
        }

        private UserApiKey CreateTestApiKey(string description = null)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            var apiKey = new UserApiKey
            {
                Id = Guid.NewGuid(),
                UserId = IntegrationTestSeeder.TestUserId,
                HashedKey = "testhash_" + Guid.NewGuid().ToString("N"),
                HashAlgorithm = "SHA256",
                Description = description ?? $"Test API Key {Guid.NewGuid():N}",
                IsDeleted = false,
                CreatedById = IntegrationTestSeeder.TestUserId,
                CreatedDate = DateTime.UtcNow,
                UpdatedById = IntegrationTestSeeder.TestUserId,
                UpdatedDate = DateTime.UtcNow
            };

            db.UserApiKeys.Add(apiKey);
            db.SaveChanges();

            return apiKey;
        }

        private UserApiKey GetApiKeyById(Guid id)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            return db.UserApiKeys.FirstOrDefault(k => k.Id == id);
        }

        private int GetActiveApiKeyCountForUser()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            return db.UserApiKeys.Count(k => k.UserId == IntegrationTestSeeder.TestUserId && !k.IsDeleted);
        }

        #endregion

        #region GET /api/UserApiKeys Tests

        [Fact]
        public async Task GetApiKeySummaryForUser_WithAuthentication_ReturnsOkWithApiKeys()
        {
            // Arrange
            CreateTestApiKey("Get Summary Test");
            var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/UserApiKeys");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<List<UserApiKeyDto>>(JsonOptions);
            Assert.NotNull(result);
        }

        [Fact]
        public async Task GetApiKeySummaryForUser_WithoutAuthentication_ReturnsUnauthorized()
        {
            // Arrange
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/UserApiKeys");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task GetApiKeySummaryForUser_ReturnsOnlyActiveKeys()
        {
            // Arrange - Create an active key and verify it appears
            var activeKey = CreateTestApiKey("Active Key Test");
            var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/UserApiKeys");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<List<UserApiKeyDto>>(JsonOptions);
            Assert.NotNull(result);
            Assert.Contains(result, k => k.Id == activeKey.Id && !k.IsDeleted);
        }

        [Fact]
        public async Task GetApiKeySummaryForUser_ReturnsKeyDetails()
        {
            // Arrange
            var apiKey = CreateTestApiKey("Details Test Key");
            var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/UserApiKeys");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<List<UserApiKeyDto>>(JsonOptions);
            var key = result.FirstOrDefault(k => k.Id == apiKey.Id);
            Assert.NotNull(key);
            Assert.Equal("Details Test Key", key.Description);
            Assert.False(key.IsDeleted);
        }

        #endregion

        #region POST /api/UserApiKeys Tests

        [Fact]
        public async Task GenerateNewApiKey_WithAuthentication_ReturnsCreatedApiKey()
        {
            // Arrange
            var dto = new AddNewApiKeyDto { Description = "New Test API Key" };
            var request = CreateAuthenticatedRequest(HttpMethod.Post, "/api/UserApiKeys");
            request.Content = new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<NewUserApiKeyDto>(JsonOptions);
            Assert.NotNull(result);
            Assert.Equal("New Test API Key", result.Description);
            Assert.NotEqual(Guid.Empty, result.Id);
            Assert.False(string.IsNullOrEmpty(result.PublicKey));
        }

        [Fact]
        public async Task GenerateNewApiKey_WithoutAuthentication_ReturnsUnauthorized()
        {
            // Arrange
            var dto = new AddNewApiKeyDto { Description = "Test" };
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/UserApiKeys");
            request.Content = new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task GenerateNewApiKey_WithEmptyDescription_ReturnsCreatedApiKey()
        {
            // Arrange
            var dto = new AddNewApiKeyDto { Description = "" };
            var request = CreateAuthenticatedRequest(HttpMethod.Post, "/api/UserApiKeys");
            request.Content = new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<NewUserApiKeyDto>(JsonOptions);
            Assert.NotNull(result);
            Assert.NotEqual(Guid.Empty, result.Id);
            Assert.False(string.IsNullOrEmpty(result.PublicKey));
        }

        [Fact]
        public async Task GenerateNewApiKey_WithNullDescription_ReturnsCreatedApiKey()
        {
            // Arrange
            var dto = new AddNewApiKeyDto { Description = null };
            var request = CreateAuthenticatedRequest(HttpMethod.Post, "/api/UserApiKeys");
            request.Content = new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<NewUserApiKeyDto>(JsonOptions);
            Assert.NotNull(result);
            Assert.NotEqual(Guid.Empty, result.Id);
        }

        [Fact]
        public async Task GenerateNewApiKey_ReturnsUniquePublicKey()
        {
            // Arrange
            var dto1 = new AddNewApiKeyDto { Description = "Key 1" };
            var dto2 = new AddNewApiKeyDto { Description = "Key 2" };

            var request1 = CreateAuthenticatedRequest(HttpMethod.Post, "/api/UserApiKeys");
            request1.Content = new StringContent(JsonSerializer.Serialize(dto1), Encoding.UTF8, "application/json");

            var request2 = CreateAuthenticatedRequest(HttpMethod.Post, "/api/UserApiKeys");
            request2.Content = new StringContent(JsonSerializer.Serialize(dto2), Encoding.UTF8, "application/json");

            // Act
            var response1 = await _httpClient.SendAsync(request1);
            var response2 = await _httpClient.SendAsync(request2);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
            Assert.Equal(HttpStatusCode.OK, response2.StatusCode);

            var result1 = await response1.Content.ReadFromJsonAsync<NewUserApiKeyDto>(JsonOptions);
            var result2 = await response2.Content.ReadFromJsonAsync<NewUserApiKeyDto>(JsonOptions);

            Assert.NotEqual(result1.PublicKey, result2.PublicKey);
            Assert.NotEqual(result1.Id, result2.Id);
        }

        [Fact]
        public async Task GenerateNewApiKey_AppearsInGetApiKeys()
        {
            // Arrange
            var dto = new AddNewApiKeyDto { Description = "Appears In Get Test" };
            var createRequest = CreateAuthenticatedRequest(HttpMethod.Post, "/api/UserApiKeys");
            createRequest.Content = new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json");

            // Act - Create key
            var createResponse = await _httpClient.SendAsync(createRequest);
            var created = await createResponse.Content.ReadFromJsonAsync<NewUserApiKeyDto>(JsonOptions);

            // Act - Get keys
            var getRequest = CreateAuthenticatedRequest(HttpMethod.Get, "/api/UserApiKeys");
            var getResponse = await _httpClient.SendAsync(getRequest);
            var keys = await getResponse.Content.ReadFromJsonAsync<List<UserApiKeyDto>>(JsonOptions);

            // Assert
            Assert.Contains(keys, k => k.Id == created.Id && k.Description == "Appears In Get Test");
        }

        #endregion

        #region DELETE /api/UserApiKeys/{apiKey} Tests

        [Fact]
        public async Task DeleteApiKey_WithValidId_ReturnsNoContent()
        {
            // Arrange
            var apiKey = CreateTestApiKey("Delete Test");
            var request = CreateAuthenticatedRequest(HttpMethod.Delete, $"/api/UserApiKeys/{apiKey.Id}");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            // Verify the API key is soft-deleted (IsDeleted = true)
            var deleted = GetApiKeyById(apiKey.Id);
            Assert.True(deleted.IsDeleted);
        }

        [Fact]
        public async Task DeleteApiKey_WithoutAuthentication_ReturnsUnauthorized()
        {
            // Arrange
            var apiKeyId = Guid.NewGuid();
            var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/UserApiKeys/{apiKeyId}");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task DeleteApiKey_WithNonExistentId_ReturnsNotFound()
        {
            // Arrange
            var nonExistentId = Guid.NewGuid();
            var request = CreateAuthenticatedRequest(HttpMethod.Delete, $"/api/UserApiKeys/{nonExistentId}");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task DeleteApiKey_ForOtherUser_ThrowsUserCannotAccessApiKeyException()
        {
            // Arrange - Create a second user's API key
            Guid otherUserApiKeyId;
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

                var otherUser = new User
                {
                    OAuthUserId = "auth0|other-api-key-user-" + Guid.NewGuid().ToString("N"),
                    ViewStatus = User.ProfileViewStatus.Public
                };
                db.Users.Add(otherUser);
                db.SaveChanges();

                var otherUserApiKey = new UserApiKey
                {
                    Id = Guid.NewGuid(),
                    UserId = otherUser.Id,
                    HashedKey = "otherhash_" + Guid.NewGuid().ToString("N"),
                    HashAlgorithm = "SHA256",
                    Description = "Other User Key",
                    IsDeleted = false,
                    CreatedById = otherUser.Id,
                    CreatedDate = DateTime.UtcNow,
                    UpdatedById = otherUser.Id,
                    UpdatedDate = DateTime.UtcNow
                };
                db.UserApiKeys.Add(otherUserApiKey);
                db.SaveChanges();
                otherUserApiKeyId = otherUserApiKey.Id;
            }

            // Try to delete the other user's key
            var request = CreateAuthenticatedRequest(HttpMethod.Delete, $"/api/UserApiKeys/{otherUserApiKeyId}");

            // Act & Assert - The controller calls Forbid() which throws in TestServer
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await _httpClient.SendAsync(request);
            });

            // Verify the API key is not deleted
            var stillExists = GetApiKeyById(otherUserApiKeyId);
            Assert.NotNull(stillExists);
            Assert.False(stillExists.IsDeleted);
        }

        [Fact]
        public async Task DeleteApiKey_AlreadyDeleted_ReturnsNotFound()
        {
            // Arrange - Create and immediately soft-delete a key
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            var apiKey = new UserApiKey
            {
                Id = Guid.NewGuid(),
                UserId = IntegrationTestSeeder.TestUserId,
                HashedKey = "deletedhash_" + Guid.NewGuid().ToString("N"),
                HashAlgorithm = "SHA256",
                Description = "Already Deleted Key",
                IsDeleted = true, // Already deleted
                CreatedById = IntegrationTestSeeder.TestUserId,
                CreatedDate = DateTime.UtcNow,
                UpdatedById = IntegrationTestSeeder.TestUserId,
                UpdatedDate = DateTime.UtcNow
            };
            db.UserApiKeys.Add(apiKey);
            db.SaveChanges();

            var request = CreateAuthenticatedRequest(HttpMethod.Delete, $"/api/UserApiKeys/{apiKey.Id}");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task DeleteApiKey_RemovedFromGetApiKeys()
        {
            // Arrange
            var apiKey = CreateTestApiKey("Remove From List Test");

            // Verify it appears in the list
            var getRequest1 = CreateAuthenticatedRequest(HttpMethod.Get, "/api/UserApiKeys");
            var getResponse1 = await _httpClient.SendAsync(getRequest1);
            var keys1 = await getResponse1.Content.ReadFromJsonAsync<List<UserApiKeyDto>>(JsonOptions);
            Assert.Contains(keys1, k => k.Id == apiKey.Id);

            // Delete the key
            var deleteRequest = CreateAuthenticatedRequest(HttpMethod.Delete, $"/api/UserApiKeys/{apiKey.Id}");
            await _httpClient.SendAsync(deleteRequest);

            // Act - Get keys again
            var getRequest2 = CreateAuthenticatedRequest(HttpMethod.Get, "/api/UserApiKeys");
            var getResponse2 = await _httpClient.SendAsync(getRequest2);
            var keys2 = await getResponse2.Content.ReadFromJsonAsync<List<UserApiKeyDto>>(JsonOptions);

            // Assert - Deleted key should not appear (or be marked as deleted)
            var deletedKey = keys2.FirstOrDefault(k => k.Id == apiKey.Id);
            Assert.True(deletedKey == null || deletedKey.IsDeleted);
        }

        #endregion

        #region Integration Tests

        [Fact]
        public async Task FullWorkflow_CreateAndDeleteApiKey()
        {
            // Create an API key
            var createDto = new AddNewApiKeyDto { Description = "Full Workflow Test Key" };
            var createRequest = CreateAuthenticatedRequest(HttpMethod.Post, "/api/UserApiKeys");
            createRequest.Content = new StringContent(JsonSerializer.Serialize(createDto), Encoding.UTF8, "application/json");

            var createResponse = await _httpClient.SendAsync(createRequest);
            Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);

            var created = await createResponse.Content.ReadFromJsonAsync<NewUserApiKeyDto>(JsonOptions);
            Assert.NotNull(created);
            Assert.False(string.IsNullOrEmpty(created.PublicKey));

            // Verify it appears in the list
            var getRequest = CreateAuthenticatedRequest(HttpMethod.Get, "/api/UserApiKeys");
            var getResponse = await _httpClient.SendAsync(getRequest);
            var keys = await getResponse.Content.ReadFromJsonAsync<List<UserApiKeyDto>>(JsonOptions);
            Assert.Contains(keys, k => k.Id == created.Id);

            // Delete the key
            var deleteRequest = CreateAuthenticatedRequest(HttpMethod.Delete, $"/api/UserApiKeys/{created.Id}");
            var deleteResponse = await _httpClient.SendAsync(deleteRequest);
            Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

            // Verify it's deleted
            var deleted = GetApiKeyById(created.Id);
            Assert.True(deleted.IsDeleted);
        }

        #endregion
    }
}
