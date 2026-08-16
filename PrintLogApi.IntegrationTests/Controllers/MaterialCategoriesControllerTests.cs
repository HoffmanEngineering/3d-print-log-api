using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using PrintLogApi.Models.DTOs.MaterialCategory;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers
{
    public class MaterialCategoriesControllerTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly HttpClient _httpClient;

        public MaterialCategoriesControllerTests(CustomWebApplicationFactory factory)
        {
            _httpClient = factory.CreateClient();
        }

        [Fact]
        public async Task GetMaterialCategories_Authenticated_ReturnsSuccess()
        {
            // Arrange
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/MaterialCategories");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task GetMaterialCategories_Authenticated_ReturnsSeededCategories()
        {
            // Arrange
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/MaterialCategories");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

            // Act
            var response = await _httpClient.SendAsync(request);
            var categories = (await response.Content.ReadFromJsonAsync<List<MaterialCategoryDto>>())!;

            // Assert - 4 categories seeded via HasData: filament, powder, resin, wire
            Assert.NotNull(categories);
            Assert.Equal(4, categories.Count);
        }

        [Fact]
        public async Task GetMaterialCategories_Authenticated_ReturnsSortedByNickname()
        {
            // Arrange
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/MaterialCategories");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

            // Act
            var response = await _httpClient.SendAsync(request);
            var categories = (await response.Content.ReadFromJsonAsync<List<MaterialCategoryDto>>())!;

            // Assert - should be sorted alphabetically by Nickname
            Assert.NotNull(categories);
            var nicknames = categories.Select(c => c.Nickname).ToList();
            Assert.Equal(nicknames.OrderBy(n => n), nicknames);
        }

        [Fact]
        public async Task GetMaterialCategories_Authenticated_ContainsFilamentCategory()
        {
            // Arrange
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/MaterialCategories");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

            // Act
            var response = await _httpClient.SendAsync(request);
            var categories = (await response.Content.ReadFromJsonAsync<List<MaterialCategoryDto>>())!;

            // Assert - verify filament category properties
            var filament = categories.Single(c => c.Nickname == "filament");
            Assert.True(filament.HasDiameter);
            Assert.True(filament.ShowNozzleTemperature);
            Assert.True(filament.ShowBedTemperature);
        }

        [Fact]
        public async Task GetMaterialCategories_NotAuthenticated_ReturnsUnauthorized()
        {
            // Arrange - no auth header
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/MaterialCategories");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }
}
