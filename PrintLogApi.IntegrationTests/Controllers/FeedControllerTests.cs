using System.Net;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Integration tests for the FeedController.
/// Note: The Feed endpoint requires:
/// 1. The user to be in an AllowedUserIds configuration list
/// 2. The Feed:AllowedUserIds configuration section to exist
/// Neither is configured in the test environment by default.
/// </summary>
public class FeedControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _httpClient;

    public FeedControllerTests(CustomWebApplicationFactory factory)
    {
        _httpClient = factory.CreateClient();
    }

    #region GET /api/Feed Tests

    [Fact]
    public async Task GetFeed_WithoutAuthentication_ReturnsUnauthorized()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Feed");

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact(Skip = "Feed endpoint requires Feed:AllowedUserIds configuration which is not set in test environment")]
    public async Task GetFeed_WithAuthentication_ReturnsNotFoundForNonAllowedUser()
    {
        // Arrange - The test user is not in the AllowedUserIds configuration list
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Feed");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert - Returns NotFound because the user is not in the allowed list
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion
}
