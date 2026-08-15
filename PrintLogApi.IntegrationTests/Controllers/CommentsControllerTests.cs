using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Comments;
using PrintLogApi.Models.DTOs.Print;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers
{
    public class CommentsControllerTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly HttpClient _httpClient;
        private readonly CustomWebApplicationFactory _factory;

        public CommentsControllerTests(CustomWebApplicationFactory factory)
        {
            _factory = factory;
            _httpClient = factory.CreateClient();
        }

        /// <summary>
        /// Helper: Gets a seeded print ID by querying the print summary endpoint.
        /// </summary>
        private async Task<long> GetSeededPrintIdAsync()
        {
            var summary = await _httpClient.GetFromJsonAsync<PagedList<PrintSummaryDTO>>(
                $"/api/Prints/summary?userId={IntegrationTestSeeder.TestUserId}");
            return summary.Items.First().Id;
        }

        /// <summary>
        /// Helper: Creates a comment on a seeded print and returns the comment detail.
        /// </summary>
        private async Task<CommentDetailDto> CreateCommentAsync(string body = "Integration test comment")
        {
            var printId = await GetSeededPrintIdAsync();

            var newComment = new AddCommentDto { Body = body };
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/Prints/{printId}/comment");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            request.Content = JsonContent.Create(newComment);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<CommentDetailDto>();
        }

        #region POST Print Comment (Create via PrintsController)

        [Fact]
        public async Task CreatePrintComment_Authenticated_ReturnsSuccess()
        {
            // Arrange
            var printId = await GetSeededPrintIdAsync();
            var newComment = new AddCommentDto { Body = "Test comment from integration test" };

            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/Prints/{printId}/comment");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            request.Content = JsonContent.Create(newComment);

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task CreatePrintComment_Authenticated_ReturnsCommentDetail()
        {
            // Arrange
            var printId = await GetSeededPrintIdAsync();
            var newComment = new AddCommentDto { Body = "Comment with detail check" };

            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/Prints/{printId}/comment");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            request.Content = JsonContent.Create(newComment);

            // Act
            var response = await _httpClient.SendAsync(request);
            var comment = (await response.Content.ReadFromJsonAsync<CommentDetailDto>())!;

            // Assert
            Assert.NotNull(comment);
            Assert.Equal("Comment with detail check", comment.Body);
            Assert.True(comment.Id > 0);
            Assert.Equal(IntegrationTestSeeder.TestUserId, comment.CreatedById);
        }

        [Fact]
        public async Task CreatePrintComment_NotAuthenticated_ReturnsUnauthorized()
        {
            // Arrange
            var printId = await GetSeededPrintIdAsync();
            var newComment = new AddCommentDto { Body = "Should not be created" };

            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/Prints/{printId}/comment");
            request.Content = JsonContent.Create(newComment);

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task CreatePrintComment_NonExistentPrint_ReturnsNotFound()
        {
            // Arrange
            var newComment = new AddCommentDto { Body = "Comment on missing print" };

            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints/999999/comment");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            request.Content = JsonContent.Create(newComment);

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        #endregion

        #region PUT Comment (Edit)

        [Fact]
        public async Task EditComment_Authenticated_ReturnsOk()
        {
            var comment = await CreateCommentAsync("Original body");

            var editDto = new EditCommentDto { Body = "Edited body" };
            var request = new HttpRequestMessage(HttpMethod.Put, $"/api/Comments/{comment.Id}");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            request.Content = JsonContent.Create(editDto);

            var response = await _httpClient.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<CommentDetailDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(result);
            Assert.Equal("Edited body", result.Body);
        }

        [Fact]
        public async Task EditComment_NotAuthenticated_ReturnsUnauthorized()
        {
            // Arrange
            var comment = await CreateCommentAsync();

            var editDto = new EditCommentDto { Body = "Should not update" };
            var request = new HttpRequestMessage(HttpMethod.Put, $"/api/Comments/{comment.Id}");
            request.Content = JsonContent.Create(editDto);

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task EditComment_NonExistent_ReturnsNotFound()
        {
            // Arrange
            var editDto = new EditCommentDto { Body = "Editing nothing" };
            var request = new HttpRequestMessage(HttpMethod.Put, "/api/Comments/999999");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            request.Content = JsonContent.Create(editDto);

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        #endregion

        #region DELETE Comment

        [Fact]
        public async Task DeleteComment_Authenticated_ReturnsOk()
        {
            // Arrange - create a comment to delete
            var comment = await CreateCommentAsync("Comment to delete");

            var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/Comments/{comment.Id}");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task DeleteComment_NotAuthenticated_ReturnsUnauthorized()
        {
            // Arrange
            var comment = await CreateCommentAsync("Comment for unauth delete test");

            var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/Comments/{comment.Id}");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task DeleteComment_NonExistent_ReturnsNotFound()
        {
            // Arrange
            var request = new HttpRequestMessage(HttpMethod.Delete, "/api/Comments/999999");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task DeleteComment_ThenEditDeleted_ReturnsNotFound()
        {
            // Arrange - create and then delete a comment
            var comment = await CreateCommentAsync("Comment to delete then edit");

            var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/Comments/{comment.Id}");
            deleteRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            await _httpClient.SendAsync(deleteRequest);

            // Act - try to edit the deleted comment
            var editDto = new EditCommentDto { Body = "Trying to edit deleted" };
            var editRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/Comments/{comment.Id}");
            editRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            editRequest.Content = JsonContent.Create(editDto);

            var response = await _httpClient.SendAsync(editRequest);

            // Assert
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task DeleteComment_WithLinkedNotification_SucceedsAndDeletesNotification()
        {
            // Arrange - create a comment, then seed a notification linked to it
            var comment = await CreateCommentAsync("Comment with linked notification");

            Guid notificationId;
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                var notification = new Notification
                {
                    Id = Guid.NewGuid(),
                    UserId = IntegrationTestSeeder.TestUserId,
                    Type = NotificationType.Comment,
                    Title = "New comment on your print",
                    IsRead = false,
                    CreatedDate = DateTime.UtcNow,
                    CommentId = comment.Id
                };
                db.Notifications.Add(notification);
                db.SaveChanges();
                notificationId = notification.Id;
            }

            // Act
            var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/Comments/{comment.Id}");
            deleteRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            var deleteResponse = await _httpClient.SendAsync(deleteRequest);

            // Assert - delete succeeded and notification was cleaned up
            Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                var orphanedNotification = db.Notifications.Find(notificationId);
                Assert.Null(orphanedNotification);
            }
        }

        #endregion
    }
}
