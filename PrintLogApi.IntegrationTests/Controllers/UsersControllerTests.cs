using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs;
using PrintLogApi.Models.DTOs.User;
using Xunit;
using static PrintLogApi.Models.User;

namespace PrintLogApi.IntegrationTests.Controllers
{
    public class UsersControllerTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly HttpClient _httpClient;
        private readonly CustomWebApplicationFactory _factory;

        public UsersControllerTests(CustomWebApplicationFactory factory)
        {
            _factory = factory;
            _httpClient = factory.CreateClient();
        }

        /// <summary>
        /// Creates a minimal test image (JPEG) for upload.
        /// </summary>
        private static ByteArrayContent CreateTestImageContent()
        {
            // Minimal JPEG header and data (a valid 1x1 pixel JPEG)
            byte[] jpegBytes = new byte[]
            {
                0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
                0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xDB, 0x00, 0x43,
                0x00, 0x08, 0x06, 0x06, 0x07, 0x06, 0x05, 0x08, 0x07, 0x07, 0x07, 0x09,
                0x09, 0x08, 0x0A, 0x0C, 0x14, 0x0D, 0x0C, 0x0B, 0x0B, 0x0C, 0x19, 0x12,
                0x13, 0x0F, 0x14, 0x1D, 0x1A, 0x1F, 0x1E, 0x1D, 0x1A, 0x1C, 0x1C, 0x20,
                0x24, 0x2E, 0x27, 0x20, 0x22, 0x2C, 0x23, 0x1C, 0x1C, 0x28, 0x37, 0x29,
                0x2C, 0x30, 0x31, 0x34, 0x34, 0x34, 0x1F, 0x27, 0x39, 0x3D, 0x38, 0x32,
                0x3C, 0x2E, 0x33, 0x34, 0x32, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x01,
                0x00, 0x01, 0x01, 0x01, 0x11, 0x00, 0xFF, 0xC4, 0x00, 0x1F, 0x00, 0x00,
                0x01, 0x05, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
                0x09, 0x0A, 0x0B, 0xFF, 0xC4, 0x00, 0xB5, 0x10, 0x00, 0x02, 0x01, 0x03,
                0x03, 0x02, 0x04, 0x03, 0x05, 0x05, 0x04, 0x04, 0x00, 0x00, 0x01, 0x7D,
                0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06,
                0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08,
                0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0, 0x24, 0x33, 0x62, 0x72,
                0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
                0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45,
                0x46, 0x47, 0x48, 0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
                0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x73, 0x74, 0x75,
                0x76, 0x77, 0x78, 0x79, 0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
                0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3,
                0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6,
                0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9,
                0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
                0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2, 0xF3, 0xF4,
                0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0xFA, 0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01,
                0x00, 0x00, 0x3F, 0x00, 0xFB, 0xD0, 0xFF, 0xD9
            };

            var content = new ByteArrayContent(jpegBytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            return content;
        }

        /// <summary>
        /// Finds a File record in the database by its path.
        /// </summary>
        private Models.File FindFileByPathInDb(string filePath)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            return db.Files
                .FirstOrDefault(f => f.Path == filePath && f.CreatedById == IntegrationTestSeeder.TestUserId);
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
        public async Task GetUserSummary_NonExistent_ReturnsNotFound()
        {
            var response = await _httpClient.GetAsync("/api/Users/999999/summary");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
        public async Task GetUserById_NonExistent_ReturnsNotFound()
        {
            var response = await _httpClient.GetAsync("/api/Users/999999");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
            var user = (await response.Content.ReadFromJsonAsync<UserDetailDto>())!;

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
            var updatedUser = (await response.Content.ReadFromJsonAsync<UserDetailDto>())!;

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
            var updatedUser = (await response.Content.ReadFromJsonAsync<UserDetailDto>())!;

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

        [Fact]
        public async Task ProcessPendingDeactivations_DeletesNotificationsReferencingDeletedPrints()
        {
            Guid notificationId;
            long userId;
            long printId;

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                var now = DateTime.UtcNow;
                var deactivatedUser = new User
                {
                    OAuthUserId = $"auth0|pending-delete-{Guid.NewGuid()}",
                    DeactivationDateTime = DateTimeOffset.UtcNow.AddDays(-2),
                    ViewStatus = ProfileViewStatus.Public
                };
                var recipientUser = new User
                {
                    OAuthUserId = $"auth0|notification-recipient-{Guid.NewGuid()}",
                    ViewStatus = ProfileViewStatus.Public
                };
                db.Users.AddRange(deactivatedUser, recipientUser);
                await db.SaveChangesAsync();

                var printer = new Printer
                {
                    Name = "Pending Delete Printer",
                    UserId = deactivatedUser.Id,
                    IsActive = true
                };
                db.Printers.Add(printer);
                await db.SaveChangesAsync();

                var print = new Print
                {
                    Title = "Pending Delete Print",
                    Status = Print.PrintStatus.Success,
                    ViewStatus = Print.PrintViewStatus.Public,
                    PrinterId = printer.Id,
                    CreatedById = deactivatedUser.Id,
                    UpdatedById = deactivatedUser.Id,
                    CreatedDate = now,
                    UpdatedDate = now
                };
                db.Prints.Add(print);
                await db.SaveChangesAsync();

                var notification = new Notification
                {
                    Id = Guid.NewGuid(),
                    UserId = recipientUser.Id,
                    Type = NotificationType.PrintCompleted,
                    Title = "Print Completed",
                    Message = "A followed print completed.",
                    IsRead = false,
                    CreatedDate = now,
                    PrintId = print.Id
                };
                db.Notifications.Add(notification);
                await db.SaveChangesAsync();

                userId = deactivatedUser.Id;
                printId = print.Id;
                notificationId = notification.Id;
            }

            var response = await _httpClient.DeleteAsync("/api/Users/pending-deactivation");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                Assert.False(await db.Users.AnyAsync(u => u.Id == userId));
                Assert.False(await db.Prints.AnyAsync(p => p.Id == printId));
                Assert.False(await db.Notifications.AnyAsync(n => n.Id == notificationId));
            }
        }

        #endregion

        #region POST Profile Image

        [Fact]
        public async Task PostProfileImage_Authenticated_ReturnsSuccess()
        {
            // Arrange
            var imageContent = CreateTestImageContent();
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/profile-image");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

            // Create multipart form data
            var formContent = new MultipartFormDataContent();
            formContent.Add(imageContent, "image", "test-profile.jpg");
            request.Content = formContent;

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task PostProfileImage_Authenticated_ReturnsUrlDto()
        {
            // Arrange
            var imageContent = CreateTestImageContent();
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/profile-image");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

            var formContent = new MultipartFormDataContent();
            formContent.Add(imageContent, "image", "test-profile-url.jpg");
            request.Content = formContent;

            // Act
            var response = await _httpClient.SendAsync(request);
            var result = (await response.Content.ReadFromJsonAsync<UserUrlDto>())!;

            // Assert
            Assert.NotNull(result);
            Assert.NotNull(result.Url);
            Assert.NotEmpty(result.Url);
            Assert.Contains("userprofile", result.Url);
        }

        [Fact]
        public async Task PostProfileImage_Authenticated_CreatesFileRecord()
        {
            // Arrange
            var imageContent = CreateTestImageContent();
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/profile-image");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

            var formContent = new MultipartFormDataContent();
            formContent.Add(imageContent, "image", "test-profile-file.jpg");
            request.Content = formContent;

            // Act
            var response = await _httpClient.SendAsync(request);
            var result = (await response.Content.ReadFromJsonAsync<UserUrlDto>())!;

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Verify File record was created
            var filePath = result.Url.Split("userprofile/").Last(); // Extract just the filename
            filePath = $"userprofile/{filePath}";
            var fileRecord = FindFileByPathInDb(filePath);
            Assert.NotNull(fileRecord);
        }

        [Fact]
        public async Task PostProfileImage_Authenticated_UpdatesUserProfilePicture()
        {
            // Arrange
            var imageContent = CreateTestImageContent();
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/profile-image");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

            var formContent = new MultipartFormDataContent();
            formContent.Add(imageContent, "image", "test-profile-update.jpg");
            request.Content = formContent;

            // Act
            var response = await _httpClient.SendAsync(request);
            var result = (await response.Content.ReadFromJsonAsync<UserUrlDto>())!;

            // Get the updated user
            var getRequest = new HttpRequestMessage(HttpMethod.Get, "/api/Users/me");
            getRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            var getResponse = await _httpClient.SendAsync(getRequest);
            var user = (await getResponse.Content.ReadFromJsonAsync<UserDetailDto>())!;

            // Assert
            Assert.NotNull(user);
            Assert.NotNull(user.ProfilePicture);
            Assert.Equal(result.Url, user.ProfilePicture);
        }

        [Fact]
        public async Task PostProfileImage_WithNoFile_ReturnsBadRequest()
        {
            // Arrange - send multipart with no image field
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/profile-image");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            request.Content = new MultipartFormDataContent();

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task PostProfileImage_WithInvalidFileType_ReturnsBadRequest()
        {
            // Arrange - send a text file instead of an image
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/profile-image");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

            var fileContent = new ByteArrayContent(new byte[] { 0x00, 0x01, 0x02 });
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            var formContent = new MultipartFormDataContent();
            formContent.Add(fileContent, "image", "document.txt");
            request.Content = formContent;

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task PostProfileImage_WithOversizedFile_ReturnsBadRequest()
        {
            // Arrange - send a file 1 byte over the 10MB limit
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/profile-image");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

            var oversizedBytes = new byte[10 * 1024 * 1024 + 1];
            var fileContent = new ByteArrayContent(oversizedBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            var formContent = new MultipartFormDataContent();
            formContent.Add(fileContent, "image", "too-big.jpg");
            request.Content = formContent;

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task PostProfileImage_NotAuthenticated_ReturnsUnauthorized()
        {
            // Arrange - no auth header
            var imageContent = CreateTestImageContent();
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/profile-image");

            var formContent = new MultipartFormDataContent();
            formContent.Add(imageContent, "image", "test-profile-unauth.jpg");
            request.Content = formContent;

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        #endregion

        #region POST Cover Image

        [Fact]
        public async Task PostCoverImage_Authenticated_ReturnsSuccess()
        {
            // Arrange
            var imageContent = CreateTestImageContent();
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/cover-image");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

            var formContent = new MultipartFormDataContent();
            formContent.Add(imageContent, "image", "test-cover.jpg");
            request.Content = formContent;

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task PostCoverImage_Authenticated_ReturnsUrlDto()
        {
            // Arrange
            var imageContent = CreateTestImageContent();
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/cover-image");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

            var formContent = new MultipartFormDataContent();
            formContent.Add(imageContent, "image", "test-cover-url.jpg");
            request.Content = formContent;

            // Act
            var response = await _httpClient.SendAsync(request);
            var result = (await response.Content.ReadFromJsonAsync<UserUrlDto>())!;

            // Assert
            Assert.NotNull(result);
            Assert.NotNull(result.Url);
            Assert.NotEmpty(result.Url);
            Assert.Contains("userprofile", result.Url);
        }

        [Fact]
        public async Task PostCoverImage_Authenticated_CreatesFileRecord()
        {
            // Arrange
            var imageContent = CreateTestImageContent();
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/cover-image");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

            var formContent = new MultipartFormDataContent();
            formContent.Add(imageContent, "image", "test-cover-file.jpg");
            request.Content = formContent;

            // Act
            var response = await _httpClient.SendAsync(request);
            var result = (await response.Content.ReadFromJsonAsync<UserUrlDto>())!;

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Verify File record was created
            var filePath = result.Url.Split("userprofile/").Last(); // Extract just the filename
            filePath = $"userprofile/{filePath}";
            var fileRecord = FindFileByPathInDb(filePath);
            Assert.NotNull(fileRecord);
        }

        [Fact]
        public async Task PostCoverImage_Authenticated_UpdatesUserCoverPicture()
        {
            // Arrange
            var imageContent = CreateTestImageContent();
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/cover-image");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

            var formContent = new MultipartFormDataContent();
            formContent.Add(imageContent, "image", "test-cover-update.jpg");
            request.Content = formContent;

            // Act
            var response = await _httpClient.SendAsync(request);
            var result = (await response.Content.ReadFromJsonAsync<UserUrlDto>())!;

            // Get the updated user
            var getRequest = new HttpRequestMessage(HttpMethod.Get, "/api/Users/me");
            getRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            var getResponse = await _httpClient.SendAsync(getRequest);
            var user = (await getResponse.Content.ReadFromJsonAsync<UserDetailDto>())!;

            // Assert
            Assert.NotNull(user);
            Assert.NotNull(user.CoverPicture);
            Assert.Equal(result.Url, user.CoverPicture);
        }

        [Fact]
        public async Task PostCoverImage_WithNoFile_ReturnsBadRequest()
        {
            // Arrange - send multipart with no image field
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/cover-image");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            request.Content = new MultipartFormDataContent();

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task PostCoverImage_WithInvalidFileType_ReturnsBadRequest()
        {
            // Arrange - send a text file instead of an image
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/cover-image");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

            var fileContent = new ByteArrayContent(new byte[] { 0x00, 0x01, 0x02 });
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            var formContent = new MultipartFormDataContent();
            formContent.Add(fileContent, "image", "document.txt");
            request.Content = formContent;

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task PostCoverImage_WithOversizedFile_ReturnsBadRequest()
        {
            // Arrange - send a file 1 byte over the 10MB limit
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/cover-image");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

            var oversizedBytes = new byte[10 * 1024 * 1024 + 1];
            var fileContent = new ByteArrayContent(oversizedBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            var formContent = new MultipartFormDataContent();
            formContent.Add(fileContent, "image", "too-big.jpg");
            request.Content = formContent;

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task PostCoverImage_NotAuthenticated_ReturnsUnauthorized()
        {
            // Arrange - no auth header
            var imageContent = CreateTestImageContent();
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/cover-image");

            var formContent = new MultipartFormDataContent();
            formContent.Add(imageContent, "image", "test-cover-unauth.jpg");
            request.Content = formContent;

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task PostCoverImage_ReplacePreviousImage_UpdatesDefault()
        {
            // Arrange - upload first cover image
            var firstImageContent = CreateTestImageContent();
            var firstRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/cover-image");
            firstRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

            var firstFormContent = new MultipartFormDataContent();
            firstFormContent.Add(firstImageContent, "image", "test-cover-first.jpg");
            firstRequest.Content = firstFormContent;

            var firstResponse = await _httpClient.SendAsync(firstRequest);
            var firstResult = (await firstResponse.Content.ReadFromJsonAsync<UserUrlDto>())!;

            // Act - upload second cover image
            var secondImageContent = CreateTestImageContent();
            var secondRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/cover-image");
            secondRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

            var secondFormContent = new MultipartFormDataContent();
            secondFormContent.Add(secondImageContent, "image", "test-cover-second.jpg");
            secondRequest.Content = secondFormContent;

            var secondResponse = await _httpClient.SendAsync(secondRequest);
            var secondResult = (await secondResponse.Content.ReadFromJsonAsync<UserUrlDto>())!;

            // Get the user - should have the second image URL
            var getRequest = new HttpRequestMessage(HttpMethod.Get, "/api/Users/me");
            getRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            var getResponse = await _httpClient.SendAsync(getRequest);
            var user = (await getResponse.Content.ReadFromJsonAsync<UserDetailDto>())!;

            // Assert
            Assert.NotNull(user.CoverPicture);
            Assert.Equal(secondResult.Url, user.CoverPicture);
            Assert.NotEqual(firstResult.Url, user.CoverPicture);
        }

        #endregion
    }
}
