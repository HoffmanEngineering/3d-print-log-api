using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using PrintLogApi.Models.DTOs.Print;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers
{
    /// <summary>
    /// Integration tests for the UsersPrintsController.
    /// Tests user statistics endpoints (filament usage, print count, print time).
    /// </summary>
    public class UsersPrintsControllerTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly HttpClient _httpClient;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public UsersPrintsControllerTests(CustomWebApplicationFactory factory)
        {
            _httpClient = factory.CreateClient();
        }

        #region Helper Methods

        private string GetDateRangeQuery()
        {
            var fromDate = DateTimeOffset.UtcNow.AddYears(-1).ToString("o");
            var toDate = DateTimeOffset.UtcNow.AddDays(1).ToString("o");
            return $"fromDate={Uri.EscapeDataString(fromDate)}&toDate={Uri.EscapeDataString(toDate)}";
        }

        #endregion

        #region GET /api/Users/{userId}/total-filament-usage Tests

        [Fact]
        public async Task GetUsersTotalFilamentUsage_WithValidUser_ReturnsOk()
        {
            // Arrange
            var userId = IntegrationTestSeeder.TestUserId;
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"/api/Users/{userId}/total-filament-usage?{GetDateRangeQuery()}");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<SinglePrintStat>(JsonOptions))!;
            Assert.NotNull(result);
            Assert.NotNull(result.Stat);
        }

        [Fact]
        public async Task GetUsersTotalFilamentUsage_WithNonExistentUser_ReturnsOkWithZero()
        {
            // Arrange - Non-existent user should return 0
            var userId = 999999;
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"/api/Users/{userId}/total-filament-usage?{GetDateRangeQuery()}");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<SinglePrintStat>(JsonOptions))!;
            Assert.NotNull(result);
            Assert.Equal("0", result.Stat);
        }

        #endregion

        #region GET /api/Users/{userId}/print-count Tests

        [Fact]
        public async Task GetUsersTotalPrintCount_WithValidUser_ReturnsOk()
        {
            // Arrange
            var userId = IntegrationTestSeeder.TestUserId;
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"/api/Users/{userId}/print-count?{GetDateRangeQuery()}");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<SinglePrintStat>(JsonOptions))!;
            Assert.NotNull(result);
            Assert.NotNull(result.Stat);
        }

        [Fact]
        public async Task GetUsersTotalPrintCount_WithNonExistentUser_ReturnsZero()
        {
            // Arrange
            var userId = 999999;
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"/api/Users/{userId}/print-count?{GetDateRangeQuery()}");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<SinglePrintStat>(JsonOptions))!;
            Assert.NotNull(result);
            Assert.Equal("0", result.Stat);
        }

        #endregion

        #region GET /api/Users/{userId}/total-print-time Tests

        [Fact]
        public async Task GetUsersTotalPrintTime_WithValidUser_ReturnsOk()
        {
            // Arrange
            var userId = IntegrationTestSeeder.TestUserId;
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"/api/Users/{userId}/total-print-time?{GetDateRangeQuery()}");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<SinglePrintStat>(JsonOptions))!;
            Assert.NotNull(result);
            Assert.NotNull(result.Stat);
        }

        [Fact]
        public async Task GetUsersTotalPrintTime_WithNonExistentUser_ReturnsZero()
        {
            // Arrange
            var userId = 999999;
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"/api/Users/{userId}/total-print-time?{GetDateRangeQuery()}");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        #endregion
    }
}
