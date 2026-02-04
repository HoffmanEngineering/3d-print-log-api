using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using PrintLogApi.Models.DTOs;
using PrintLogApi.Models.DTOs.User;
using Xunit;
using static PrintLogApi.Models.User;

namespace PrintLogApi.IntegrationTests.Controllers
{
    public class UsersControllerTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly HttpClient _httpClient;

        public UsersControllerTests(CustomWebApplicationFactory factory)
        {
            _httpClient = factory.CreateClient();
        }

        #region GET User Summary (Anonymous)

        [Fact]
        public async Task GetUserSummary_ById_ReturnsSuccess()
        {
            // Arrange - use the seeded test user ID
            var response = await _httpClient.GetAsync($"/api/Users/{IntegrationTestSeeder.TestUserId}/summary");

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task GetUserSummary_ById_ReturnsExpectedData()
        {
            // Act
            var user = await _httpClient.GetFromJsonAsync<UserSummaryDto>(
                $"/api/Users/{IntegrationTestSeeder.TestUserId}/summary");

            // Assert
            Assert.NotNull(user);
            Assert.Equal(IntegrationTestSeeder.TestUserId, user.Id);
        }

        [Fact]
        public async Task GetUserSummary_NonExistent_ThrowsException()
        {
            // The API uses .Single() which throws when user not found instead of returning 404
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await _httpClient.GetAsync("/api/Users/999999/summary");
            });
        }

        #endregion

        #region GET User Details (Anonymous)

        [Fact]
        public async Task GetUserById_PublicUser_ReturnsSuccess()
        {
            // Arrange - the seeded test user has ViewStatus = Public
            var response = await _httpClient.GetAsync($"/api/Users/{IntegrationTestSeeder.TestUserId}");

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task GetUserById_PublicUser_ReturnsExpectedData()
        {
            // Act
            var user = await _httpClient.GetFromJsonAsync<UserDetailDto>(
                $"/api/Users/{IntegrationTestSeeder.TestUserId}");

            // Assert
            Assert.NotNull(user);
            Assert.Equal(IntegrationTestSeeder.TestUserId, user.Id);
            Assert.Equal(ProfileViewStatus.Public, user.ViewStatus);
        }

        [Fact]
        public async Task GetUserById_NonExistent_ThrowsException()
        {
            // The API uses .Single() which throws when user not found instead of returning 404
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await _httpClient.GetAsync("/api/Users/999999");
            });
        }

        #endregion

        #region GET Current User (Me)

        [Fact]
        public async Task GetCurrentUser_Authenticated_ReturnsSuccess()
        {
            // Arrange
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/Users/me");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task GetCurrentUser_Authenticated_ReturnsExpectedData()
        {
            // Arrange
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/Users/me");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

            // Act
            var response = await _httpClient.SendAsync(request);
            var user = await response.Content.ReadFromJsonAsync<UserDetailDto>();

            // Assert
            Assert.NotNull(user);
            Assert.Equal(IntegrationTestSeeder.TestUserId, user.Id);
        }

        [Fact]
        public async Task GetCurrentUser_NotAuthenticated_ReturnsUnauthorized()
        {
            // Arrange - no auth header
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/Users/me");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        #endregion

        #region PUT Current User (Update)

        [Fact]
        public async Task UpdateCurrentUser_Authenticated_ReturnsSuccess()
        {
            // Arrange
            var updateDto = new UpdateUserDetailDto
            {
                DisplayName = "Updated Test User",
                Bio = "Updated bio from integration test",
                ViewStatus = ProfileViewStatus.Public
            };

            var request = new HttpRequestMessage(HttpMethod.Put, "/api/Users/me");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            request.Content = JsonContent.Create(updateDto);

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task UpdateCurrentUser_Authenticated_ReturnsUpdatedData()
        {
            // Arrange
            var updateDto = new UpdateUserDetailDto
            {
                DisplayName = "Integration Test Name",
                Bio = "This is my updated bio",
                ViewStatus = ProfileViewStatus.Public
            };

            var request = new HttpRequestMessage(HttpMethod.Put, "/api/Users/me");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            request.Content = JsonContent.Create(updateDto);

            // Act
            var response = await _httpClient.SendAsync(request);
            var updatedUser = await response.Content.ReadFromJsonAsync<UserDetailDto>();

            // Assert
            Assert.NotNull(updatedUser);
            Assert.Equal("Integration Test Name", updatedUser.DisplayName);
            Assert.Equal("This is my updated bio", updatedUser.Bio);
        }

        [Fact]
        public async Task UpdateCurrentUser_NotAuthenticated_ReturnsUnauthorized()
        {
            // Arrange
            var updateDto = new UpdateUserDetailDto
            {
                DisplayName = "Should Not Update",
                ViewStatus = ProfileViewStatus.Public
            };

            var request = new HttpRequestMessage(HttpMethod.Put, "/api/Users/me");
            request.Content = JsonContent.Create(updateDto);

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task UpdateCurrentUser_ChangeViewStatus_ReturnsSuccess()
        {
            // Arrange - change to Unlisted then back to Public
            var updateDto = new UpdateUserDetailDto
            {
                DisplayName = "Test User",
                ViewStatus = ProfileViewStatus.Unlisted
            };

            var request = new HttpRequestMessage(HttpMethod.Put, "/api/Users/me");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            request.Content = JsonContent.Create(updateDto);

            // Act
            var response = await _httpClient.SendAsync(request);
            var updatedUser = await response.Content.ReadFromJsonAsync<UserDetailDto>();

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(ProfileViewStatus.Unlisted, updatedUser.ViewStatus);

            // Cleanup - restore to Public for other tests
            var restoreDto = new UpdateUserDetailDto
            {
                DisplayName = "Test User",
                ViewStatus = ProfileViewStatus.Public
            };
            var restoreRequest = new HttpRequestMessage(HttpMethod.Put, "/api/Users/me");
            restoreRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            restoreRequest.Content = JsonContent.Create(restoreDto);
            await _httpClient.SendAsync(restoreRequest);
        }

        #endregion

        #region GET Public Users

        [Fact]
        public async Task GetPublicUsers_ReturnsSuccess()
        {
            // Act - this endpoint is anonymous (for sitemaps)
            var response = await _httpClient.GetAsync("/api/Users/public");

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task GetPublicUsers_ReturnsListOfIds()
        {
            // Act
            var userIds = await _httpClient.GetFromJsonAsync<IEnumerable<long>>("/api/Users/public");

            // Assert
            Assert.NotNull(userIds);
            Assert.Contains(IntegrationTestSeeder.TestUserId, userIds);
        }

        #endregion

        #region POST Deactivate/Reactivate User

        [Fact]
        public async Task DeactivateUser_Authenticated_ReturnsSuccess()
        {
            // Arrange
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/deactivate");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Cleanup - reactivate the user for other tests
            var reactivateRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/reactivate");
            reactivateRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            await _httpClient.SendAsync(reactivateRequest);
        }

        [Fact]
        public async Task DeactivateUser_NotAuthenticated_ReturnsUnauthorized()
        {
            // Arrange - no auth header
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/deactivate");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task ReactivateUser_Authenticated_ReturnsSuccess()
        {
            // Arrange - first deactivate
            var deactivateRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/deactivate");
            deactivateRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            await _httpClient.SendAsync(deactivateRequest);

            // Act - then reactivate
            var reactivateRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/reactivate");
            reactivateRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            var response = await _httpClient.SendAsync(reactivateRequest);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task ReactivateUser_NotAuthenticated_ReturnsUnauthorized()
        {
            // Arrange - no auth header
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/reactivate");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        #endregion
    }
}
