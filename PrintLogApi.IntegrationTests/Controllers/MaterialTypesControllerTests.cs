using System.Net;
using PrintLogApi.Models.DTOs.Materials;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

public class MaterialTypesControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _httpClient;

    public MaterialTypesControllerTests(CustomWebApplicationFactory factory)
    {
        _httpClient = factory.CreateClient();
    }

    [Fact]
    public async Task GetMaterialTypes_Authenticated_ReturnsSuccess()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/MaterialTypes");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetMaterialTypes_Authenticated_ReturnsSeededTypes()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/MaterialTypes");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request);
        var types = (await response.Content.ReadFromJsonAsync<List<MaterialTypeDto>>())!;

        // Assert - multiple material types seeded via HasData
        Assert.NotNull(types);
        Assert.True(types.Count > 0);
    }

    [Fact]
    public async Task GetMaterialTypes_Authenticated_ReturnsSortedByAcronym()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/MaterialTypes");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request);
        var types = (await response.Content.ReadFromJsonAsync<List<MaterialTypeDto>>())!;

        // Assert - should be sorted alphabetically by Acronym
        Assert.NotNull(types);
        var acronyms = types.Select(t => t.Acronym).ToList();
        Assert.Equal(acronyms.OrderBy(a => a), acronyms);
    }

    [Fact]
    public async Task GetMaterialTypes_Authenticated_ContainsPLA()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/MaterialTypes");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request);
        var types = (await response.Content.ReadFromJsonAsync<List<MaterialTypeDto>>())!;

        // Assert - PLA should be in the seeded data
        var pla = types.Single(t => t.Acronym == "PLA");
        Assert.Equal("filament", pla.MaterialCategoryNickname);
        Assert.True(pla.DensityGramPerCubicCm > 0);
    }

    [Fact]
    public async Task GetMaterialTypes_Authenticated_AllSeededTypesHaveAName()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/MaterialTypes");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request);
        var types = (await response.Content.ReadFromJsonAsync<List<MaterialTypeDto>>())!;

        // Assert - Name is the expanded form of Acronym and is what the material
        // library renders, so a blank one shows up as an empty row to the user.
        // Asserted over the whole set rather than one type, to catch the next
        // seeded material that ships without a name.
        var unnamed = types
            .Where(t => string.IsNullOrWhiteSpace(t.Name))
            .Select(t => t.Acronym)
            .ToList();
        Assert.Empty(unnamed);
    }

    [Fact]
    public async Task GetMaterialTypes_AlternateRoute_ReturnsSuccess()
    {
        // Arrange - the controller also responds on /api/Materials
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Materials");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetMaterialTypes_NotAuthenticated_ReturnsUnauthorized()
    {
        // Arrange - no auth header
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/MaterialTypes");

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
