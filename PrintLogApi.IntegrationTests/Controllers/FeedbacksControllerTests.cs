using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using PrintLogApi.Models.DTOs.Feedback;
using Xunit;
using static PrintLogApi.Models.Feedback;

namespace PrintLogApi.IntegrationTests.Controllers
{
    public class FeedbacksControllerTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly HttpClient _httpClient;

        public FeedbacksControllerTests(CustomWebApplicationFactory factory)
        {
            _httpClient = factory.CreateClient();
        }

        #region POST Feedback (Create)

        [Fact]
        public async Task CreateFeedback_Authenticated_ReturnsCreated()
        {
            // Arrange
            var feedback = new AddFeedbackDto
            {
                Type = FeedbackType.Suggestion,
                Note = "This is a test suggestion from integration tests"
            };

            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Feedbacks");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            request.Content = JsonContent.Create(feedback);

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        [Fact]
        public async Task CreateFeedback_Authenticated_WithEmail_ReturnsCreated()
        {
            // Arrange
            var feedback = new AddFeedbackDto
            {
                Type = FeedbackType.Question,
                Email = "test@example.com",
                Note = "This is a test question with email"
            };

            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Feedbacks");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            request.Content = JsonContent.Create(feedback);

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        [Fact]
        public async Task CreateFeedback_Authenticated_BugType_ReturnsCreated()
        {
            // Arrange
            var feedback = new AddFeedbackDto
            {
                Type = FeedbackType.Bug,
                Note = "Found a bug during testing"
            };

            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Feedbacks");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            request.Content = JsonContent.Create(feedback);

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        [Fact]
        public async Task CreateFeedback_Authenticated_OtherType_ReturnsCreated()
        {
            // Arrange
            var feedback = new AddFeedbackDto
            {
                Type = FeedbackType.Other,
                Note = "Other type of feedback"
            };

            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Feedbacks");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            request.Content = JsonContent.Create(feedback);

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        [Fact]
        public async Task CreateFeedback_NotAuthenticated_ReturnsUnauthorized()
        {
            // Arrange
            var feedback = new AddFeedbackDto
            {
                Type = FeedbackType.Suggestion,
                Note = "This should not be created"
            };

            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Feedbacks");
            request.Content = JsonContent.Create(feedback);

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task CreateFeedback_Authenticated_WithEmptyNote_ReturnsCreated()
        {
            // Arrange - Note is optional
            var feedback = new AddFeedbackDto
            {
                Type = FeedbackType.Question
            };

            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Feedbacks");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            request.Content = JsonContent.Create(feedback);

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        #endregion
    }
}
