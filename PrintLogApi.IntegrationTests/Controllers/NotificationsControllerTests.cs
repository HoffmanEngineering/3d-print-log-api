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
using PrintLogApi.Models.DTOs.Notification;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers
{
    /// <summary>
    /// Integration tests for the NotificationsController.
    /// Tests notification CRUD operations, read status management, and bulk operations.
    /// </summary>
    public class NotificationsControllerTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly HttpClient _httpClient;
        private readonly CustomWebApplicationFactory _factory;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public NotificationsControllerTests(CustomWebApplicationFactory factory)
        {
            _factory = factory;
            _httpClient = factory.CreateClient();
        }

        #region Helper Methods

        private HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string url, string? userId = null)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, userId ?? IntegrationTestSeeder.TestUserOAuthId);
            return request;
        }

        private Notification CreateTestNotification(string? title = null, bool isRead = false)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            var notification = new Notification
            {
                Id = Guid.NewGuid(),
                UserId = IntegrationTestSeeder.TestUserId,
                Type = NotificationType.SystemAnnouncement,
                Title = title ?? $"Test Notification {Guid.NewGuid():N}",
                Message = "Test notification message",
                IsRead = isRead,
                CreatedDate = DateTime.UtcNow,
                ReadDate = isRead ? DateTime.UtcNow : null
            };

            db.Notifications.Add(notification);
            db.SaveChanges();

            return notification;
        }

        private Notification? GetNotificationById(Guid id)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            return db.Notifications.FirstOrDefault(n => n.Id == id);
        }

        private int GetUnreadCountForUser()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            return db.Notifications.Count(n => n.UserId == IntegrationTestSeeder.TestUserId && !n.IsRead);
        }

        private int GetTotalNotificationCountForUser()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            return db.Notifications.Count(n => n.UserId == IntegrationTestSeeder.TestUserId);
        }

        #endregion

        #region GET /api/Notifications Tests

        [Fact]
        public async Task GetNotifications_WithAuthentication_ReturnsOkWithNotifications()
        {
            // Arrange
            var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/Notifications");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var content = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<PagedList<NotificationSummaryDto>>(content, JsonOptions);
            Assert.NotNull(result);
            Assert.True(result.Items.Count > 0);
        }

        [Fact]
        public async Task GetNotifications_WithoutAuthentication_ReturnsUnauthorized()
        {
            // Arrange
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/Notifications");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task GetNotifications_WithPagination_ReturnsPagedResults()
        {
            // Arrange
            var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/Notifications?pageSize=2&page=1");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var content = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<PagedList<NotificationSummaryDto>>(content, JsonOptions);
            Assert.NotNull(result);
            Assert.True(result.Items.Count <= 2);
            Assert.Equal(1, result.Paging.CurrentPage);
        }

        [Fact]
        public async Task GetNotifications_WithUnreadOnlyFilter_ReturnsOnlyUnreadNotifications()
        {
            // Arrange
            var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/Notifications?unreadOnly=true");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var content = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<PagedList<NotificationSummaryDto>>(content, JsonOptions);
            Assert.NotNull(result);
            Assert.All(result.Items, n => Assert.False(n.IsRead));
        }

        [Fact]
        public async Task GetNotifications_WithUnreadOnlyFalse_ReturnsAllNotifications()
        {
            // Arrange
            var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/Notifications?unreadOnly=false");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var content = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<PagedList<NotificationSummaryDto>>(content, JsonOptions);
            Assert.NotNull(result);
            // Should include both read and unread notifications
            var totalCount = GetTotalNotificationCountForUser();
            Assert.True(result.Paging.TotalCount >= totalCount || result.Items.Count > 0);
        }

        [Fact]
        public async Task GetNotifications_ReturnsNotificationsOrderedByCreatedDateDescending()
        {
            // Arrange
            var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/Notifications");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var content = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<PagedList<NotificationSummaryDto>>(content, JsonOptions);
            Assert.NotNull(result);

            if (result.Items.Count > 1)
            {
                for (int i = 0; i < result.Items.Count - 1; i++)
                {
                    Assert.True(result.Items[i].CreatedDate >= result.Items[i + 1].CreatedDate);
                }
            }
        }

        #endregion

        #region GET /api/Notifications/unread-count Tests

        [Fact]
        public async Task GetUnreadCount_WithAuthentication_ReturnsOkWithCount()
        {
            // Arrange
            var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/Notifications/unread-count");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<NotificationUnreadCountDto>(JsonOptions))!;
            Assert.NotNull(result);
            Assert.True(result.UnreadCount >= 0);
        }

        [Fact]
        public async Task GetUnreadCount_WithoutAuthentication_ReturnsUnauthorized()
        {
            // Arrange
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/Notifications/unread-count");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task GetUnreadCount_MatchesActualUnreadCount()
        {
            // Arrange
            var expectedCount = GetUnreadCountForUser();
            var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/Notifications/unread-count");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<NotificationUnreadCountDto>(JsonOptions))!;
            Assert.NotNull(result);
            Assert.Equal(expectedCount, result.UnreadCount);
        }

        #endregion

        #region GET /api/Notifications/{id} Tests

        [Fact]
        public async Task GetNotification_WithValidId_ReturnsOkWithNotification()
        {
            // Arrange - Create a fresh notification to avoid race conditions with delete tests
            var notification = CreateTestNotification("Get Valid Test");
            var request = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/Notifications/{notification.Id}");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<NotificationDetailDto>(JsonOptions))!;
            Assert.NotNull(result);
            Assert.Equal(notification.Id, result.Id);
            Assert.Equal("Get Valid Test", result.Title);
        }

        [Fact]
        public async Task GetNotification_WithoutAuthentication_ReturnsUnauthorized()
        {
            // Arrange
            var notificationId = IntegrationTestSeeder.TestNotificationId1;
            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Notifications/{notificationId}");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task GetNotification_WithNonExistentId_ReturnsNotFound()
        {
            // Arrange
            var nonExistentId = Guid.NewGuid();
            var request = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/Notifications/{nonExistentId}");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task GetNotification_ForOtherUser_ReturnsUnauthorized()
        {
            // Arrange - A non-existent user cannot access any notifications
            var notification = CreateTestNotification("Other User Get Test");
            var request = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/Notifications/{notification.Id}", "auth0|different-user");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert - Returns Unauthorized because the user doesn't exist in the system
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task GetNotification_ReturnsDetailDto()
        {
            // Arrange
            var notification = CreateTestNotification("Detail Test Notification");
            var request = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/Notifications/{notification.Id}");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<NotificationDetailDto>(JsonOptions))!;
            Assert.NotNull(result);
            Assert.Equal(notification.Id, result.Id);
            Assert.Equal(notification.Title, result.Title);
            Assert.Equal(notification.Message, result.Message);
            Assert.Equal(notification.Type, result.Type);
            Assert.Equal(notification.IsRead, result.IsRead);
        }

        #endregion

        #region PUT /api/Notifications/{id}/read Tests

        [Fact]
        public async Task MarkAsRead_WithValidUnreadNotification_ReturnsNoContent()
        {
            // Arrange
            var notification = CreateTestNotification("Mark As Read Test", isRead: false);
            var request = CreateAuthenticatedRequest(HttpMethod.Put, $"/api/Notifications/{notification.Id}/read");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            // Verify the notification is marked as read
            var updated = GetNotificationById(notification.Id)!;
            Assert.True(updated.IsRead);
            Assert.NotNull(updated.ReadDate);
        }

        [Fact]
        public async Task MarkAsRead_WithoutAuthentication_ReturnsUnauthorized()
        {
            // Arrange
            var notificationId = IntegrationTestSeeder.TestNotificationId1;
            var request = new HttpRequestMessage(HttpMethod.Put, $"/api/Notifications/{notificationId}/read");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task MarkAsRead_WithNonExistentId_ReturnsNotFound()
        {
            // Arrange
            var nonExistentId = Guid.NewGuid();
            var request = CreateAuthenticatedRequest(HttpMethod.Put, $"/api/Notifications/{nonExistentId}/read");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task MarkAsRead_ForOtherUser_ReturnsUnauthorized()
        {
            // Arrange
            var notification = CreateTestNotification("Other User Read Test", isRead: false);
            var request = CreateAuthenticatedRequest(HttpMethod.Put, $"/api/Notifications/{notification.Id}/read", "auth0|different-user");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert - Returns Unauthorized because the user doesn't exist in the system
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            // Verify the notification is still unread
            var unchanged = GetNotificationById(notification.Id)!;
            Assert.False(unchanged.IsRead);
        }

        [Fact]
        public async Task MarkAsRead_AlreadyReadNotification_ReturnsNoContent()
        {
            // Arrange
            var notification = CreateTestNotification("Already Read Test", isRead: true);
            var request = CreateAuthenticatedRequest(HttpMethod.Put, $"/api/Notifications/{notification.Id}/read");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        #endregion

        #region PUT /api/Notifications/read-all Tests

        [Fact]
        public async Task MarkAllAsRead_WithAuthentication_ReturnsNoContent()
        {
            // Arrange - Create some unread notifications
            CreateTestNotification("MarkAll Test 1", isRead: false);
            CreateTestNotification("MarkAll Test 2", isRead: false);

            var request = CreateAuthenticatedRequest(HttpMethod.Put, "/api/Notifications/read-all");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            // Verify unread count is 0
            var unreadCount = GetUnreadCountForUser();
            Assert.Equal(0, unreadCount);
        }

        [Fact]
        public async Task MarkAllAsRead_WithoutAuthentication_ReturnsUnauthorized()
        {
            // Arrange
            var request = new HttpRequestMessage(HttpMethod.Put, "/api/Notifications/read-all");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task MarkAllAsRead_WithNoUnreadNotifications_ReturnsNoContent()
        {
            // Arrange - First mark all as read
            var markAllRequest = CreateAuthenticatedRequest(HttpMethod.Put, "/api/Notifications/read-all");
            await _httpClient.SendAsync(markAllRequest);

            // Then try again
            var request = CreateAuthenticatedRequest(HttpMethod.Put, "/api/Notifications/read-all");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        #endregion

        #region PUT /api/Notifications/read (Multiple) Tests

        [Fact]
        public async Task MarkMultipleAsRead_WithValidIds_ReturnsNoContent()
        {
            // Arrange
            var notification1 = CreateTestNotification("Multiple Read Test 1", isRead: false);
            var notification2 = CreateTestNotification("Multiple Read Test 2", isRead: false);

            var dto = new MarkNotificationsReadDto
            {
                NotificationIds = new List<Guid> { notification1.Id, notification2.Id }
            };

            var request = CreateAuthenticatedRequest(HttpMethod.Put, "/api/Notifications/read");
            request.Content = new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            // Verify both notifications are marked as read
            var updated1 = GetNotificationById(notification1.Id)!;
            var updated2 = GetNotificationById(notification2.Id)!;
            Assert.True(updated1.IsRead);
            Assert.True(updated2.IsRead);
        }

        [Fact]
        public async Task MarkMultipleAsRead_WithoutAuthentication_ReturnsUnauthorized()
        {
            // Arrange
            var dto = new MarkNotificationsReadDto
            {
                NotificationIds = new List<Guid> { Guid.NewGuid() }
            };

            var request = new HttpRequestMessage(HttpMethod.Put, "/api/Notifications/read");
            request.Content = new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task MarkMultipleAsRead_WithEmptyList_ReturnsBadRequest()
        {
            // Arrange
            var dto = new MarkNotificationsReadDto
            {
                NotificationIds = new List<Guid>()
            };

            var request = CreateAuthenticatedRequest(HttpMethod.Put, "/api/Notifications/read");
            request.Content = new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task MarkMultipleAsRead_WithNullList_ReturnsBadRequest()
        {
            // Arrange
            var dto = new MarkNotificationsReadDto
            {
                NotificationIds = null
            };

            var request = CreateAuthenticatedRequest(HttpMethod.Put, "/api/Notifications/read");
            request.Content = new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task MarkMultipleAsRead_WithTooManyIds_ReturnsBadRequest()
        {
            // Arrange - Create list with 101 IDs (max is 100)
            var ids = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToList();
            var dto = new MarkNotificationsReadDto
            {
                NotificationIds = ids
            };

            var request = CreateAuthenticatedRequest(HttpMethod.Put, "/api/Notifications/read");
            request.Content = new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var content = await response.Content.ReadAsStringAsync();
            Assert.Contains("100", content);
        }

        [Fact]
        public async Task MarkMultipleAsRead_WithNonExistentIds_ReturnsNoContent()
        {
            // Arrange - Non-existent IDs are silently ignored
            var dto = new MarkNotificationsReadDto
            {
                NotificationIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() }
            };

            var request = CreateAuthenticatedRequest(HttpMethod.Put, "/api/Notifications/read");
            request.Content = new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        [Fact]
        public async Task MarkMultipleAsRead_WithMixedValidAndInvalidIds_MarksValidOnes()
        {
            // Arrange
            var validNotification = CreateTestNotification("Mixed Valid Test", isRead: false);
            var dto = new MarkNotificationsReadDto
            {
                NotificationIds = new List<Guid> { validNotification.Id, Guid.NewGuid() }
            };

            var request = CreateAuthenticatedRequest(HttpMethod.Put, "/api/Notifications/read");
            request.Content = new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            // Verify valid notification is marked as read
            var updated = GetNotificationById(validNotification.Id)!;
            Assert.True(updated.IsRead);
        }

        #endregion

        #region DELETE /api/Notifications/{id} Tests

        [Fact]
        public async Task DeleteNotification_WithValidId_ReturnsNoContent()
        {
            // Arrange
            var notification = CreateTestNotification("Delete Test");
            var request = CreateAuthenticatedRequest(HttpMethod.Delete, $"/api/Notifications/{notification.Id}");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            // Verify the notification is deleted
            var deleted = GetNotificationById(notification.Id)!;
            Assert.Null(deleted);
        }

        [Fact]
        public async Task DeleteNotification_WithoutAuthentication_ReturnsUnauthorized()
        {
            // Arrange
            var notificationId = Guid.NewGuid();
            var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/Notifications/{notificationId}");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task DeleteNotification_WithNonExistentId_ReturnsNotFound()
        {
            // Arrange
            var nonExistentId = Guid.NewGuid();
            var request = CreateAuthenticatedRequest(HttpMethod.Delete, $"/api/Notifications/{nonExistentId}");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task DeleteNotification_ForOtherUser_ReturnsUnauthorized()
        {
            // Arrange
            var notification = CreateTestNotification("Other User Delete Test");
            var request = CreateAuthenticatedRequest(HttpMethod.Delete, $"/api/Notifications/{notification.Id}", "auth0|different-user");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert - Returns Unauthorized because the user doesn't exist in the system
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            // Verify the notification still exists
            var stillExists = GetNotificationById(notification.Id)!;
            Assert.NotNull(stillExists);
        }

        #endregion

        #region DELETE /api/Notifications Tests

        [Fact]
        public async Task DeleteAllNotifications_WithAuthentication_ReturnsNoContent()
        {
            // Arrange - Create some notifications to delete
            CreateTestNotification("DeleteAll Test 1");
            CreateTestNotification("DeleteAll Test 2");

            var request = CreateAuthenticatedRequest(HttpMethod.Delete, "/api/Notifications");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            // Verify all notifications are deleted
            var count = GetTotalNotificationCountForUser();
            Assert.Equal(0, count);
        }

        [Fact]
        public async Task DeleteAllNotifications_WithoutAuthentication_ReturnsUnauthorized()
        {
            // Arrange
            var request = new HttpRequestMessage(HttpMethod.Delete, "/api/Notifications");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task DeleteAllNotifications_WithNoNotifications_ReturnsNoContent()
        {
            // Arrange - First delete all
            var deleteAllRequest = CreateAuthenticatedRequest(HttpMethod.Delete, "/api/Notifications");
            await _httpClient.SendAsync(deleteAllRequest);

            // Then try again
            var request = CreateAuthenticatedRequest(HttpMethod.Delete, "/api/Notifications");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        #endregion

        #region Integration Tests

        [Fact]
        public async Task FullWorkflow_CreateReadMarkDeleteNotification()
        {
            // Create a notification
            var notification = CreateTestNotification("Workflow Test", isRead: false);

            // Read the notification
            var getRequest = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/Notifications/{notification.Id}");
            var getResponse = await _httpClient.SendAsync(getRequest);
            Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
            var detail = (await getResponse.Content.ReadFromJsonAsync<NotificationDetailDto>(JsonOptions))!;
            Assert.False(detail.IsRead);

            // Mark as read
            var markRequest = CreateAuthenticatedRequest(HttpMethod.Put, $"/api/Notifications/{notification.Id}/read");
            var markResponse = await _httpClient.SendAsync(markRequest);
            Assert.Equal(HttpStatusCode.NoContent, markResponse.StatusCode);

            // Verify it's read
            var verifyRequest = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/Notifications/{notification.Id}");
            var verifyResponse = await _httpClient.SendAsync(verifyRequest);
            var verifiedDetail = (await verifyResponse.Content.ReadFromJsonAsync<NotificationDetailDto>(JsonOptions))!;
            Assert.True(verifiedDetail.IsRead);
            Assert.NotNull(verifiedDetail.ReadDate);

            // Delete the notification
            var deleteRequest = CreateAuthenticatedRequest(HttpMethod.Delete, $"/api/Notifications/{notification.Id}");
            var deleteResponse = await _httpClient.SendAsync(deleteRequest);
            Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

            // Verify it's deleted
            var deletedRequest = CreateAuthenticatedRequest(HttpMethod.Get, $"/api/Notifications/{notification.Id}");
            var deletedResponse = await _httpClient.SendAsync(deletedRequest);
            Assert.Equal(HttpStatusCode.NotFound, deletedResponse.StatusCode);
        }

        [Fact]
        public async Task UnreadCount_UpdatesAfterMarkingAsRead()
        {
            // Create unread notification
            var notification = CreateTestNotification("Unread Count Test", isRead: false);

            // Get initial unread count
            var countRequest1 = CreateAuthenticatedRequest(HttpMethod.Get, "/api/Notifications/unread-count");
            var countResponse1 = await _httpClient.SendAsync(countRequest1);
            var count1 = (await countResponse1.Content.ReadFromJsonAsync<NotificationUnreadCountDto>(JsonOptions))!;
            var initialCount = count1.UnreadCount;

            // Mark as read
            var markRequest = CreateAuthenticatedRequest(HttpMethod.Put, $"/api/Notifications/{notification.Id}/read");
            await _httpClient.SendAsync(markRequest);

            // Get updated unread count
            var countRequest2 = CreateAuthenticatedRequest(HttpMethod.Get, "/api/Notifications/unread-count");
            var countResponse2 = await _httpClient.SendAsync(countRequest2);
            var count2 = (await countResponse2.Content.ReadFromJsonAsync<NotificationUnreadCountDto>(JsonOptions))!;

            // Verify count decreased
            Assert.Equal(initialCount - 1, count2.UnreadCount);
        }

        #endregion
    }
}
