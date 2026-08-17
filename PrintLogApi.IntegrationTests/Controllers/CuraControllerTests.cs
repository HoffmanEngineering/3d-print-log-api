using System.Net;
using System.Text;
using System.Text.Json;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.CuraSettings;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

public class CuraControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _httpClient;

    public CuraControllerTests(CustomWebApplicationFactory factory)
    {
        _httpClient = factory.CreateClient();
    }

    /// <summary>
    /// Helper: Saves Cura settings via POST and returns the new setting GUID.
    /// </summary>
    private async Task<Guid> SaveCuraSettingsAsync(
        string curaVersion = "5.4.0",
        string pluginVersion = "1.0.0")
    {
        var settingsJson = JsonSerializer.Serialize(new
        {
            curaVersion,
            pluginVersion,
            settings = new { layerHeight = 0.2, infillDensity = 20 }
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Cura/settings");
        request.Content = new StringContent(settingsJson, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var result = (await response.Content.ReadFromJsonAsync<NewCuraSettingsDto>())!;
        return result.NewSettingId;
    }

    #region POST Settings (Save - Anonymous)

    [Fact]
    public async Task SaveSettings_Anonymous_ReturnsSuccess()
    {
        // Arrange
        var settingsJson = JsonSerializer.Serialize(new
        {
            curaVersion = "5.4.0",
            pluginVersion = "1.0.0",
            settings = new { layerHeight = 0.2 }
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Cura/settings");
        request.Content = new StringContent(settingsJson, Encoding.UTF8, "application/json");

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SaveSettings_Anonymous_ReturnsNewGuid()
    {
        // Arrange
        var settingsJson = JsonSerializer.Serialize(new
        {
            curaVersion = "5.4.0",
            pluginVersion = "1.0.0",
            settings = new { infillDensity = 20 }
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Cura/settings");
        request.Content = new StringContent(settingsJson, Encoding.UTF8, "application/json");

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var result = (await response.Content.ReadFromJsonAsync<NewCuraSettingsDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Assert
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.NewSettingId);
    }

    [Fact]
    public async Task SaveSettings_MultipleCalls_ReturnsDifferentGuids()
    {
        // Act
        var guid1 = await SaveCuraSettingsAsync();
        var guid2 = await SaveCuraSettingsAsync();

        // Assert
        Assert.NotEqual(guid1, guid2);
    }

    #endregion

    #region GET Settings (Retrieve - Authenticated)

    [Fact]
    public async Task GetSettings_Authenticated_ReturnsSuccess()
    {
        // Arrange - save settings first, then retrieve
        var settingId = await SaveCuraSettingsAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Cura/settings?id={settingId}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetSettings_Authenticated_ReturnsSettingsData()
    {
        // Arrange
        var settingId = await SaveCuraSettingsAsync("5.5.0", "2.0.0");

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Cura/settings?id={settingId}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var settings = (await response.Content.ReadFromJsonAsync<CuraSetting>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Assert
        Assert.NotNull(settings);
        Assert.Equal(settingId, settings.Id);
        Assert.Equal("5.5.0", settings.CuraVersion);
        Assert.Equal("2.0.0", settings.PluginVersion);
    }

    [Fact]
    public async Task GetSettings_Authenticated_LocksToUser()
    {
        // Arrange - save and retrieve to lock the setting to the test user
        var settingId = await SaveCuraSettingsAsync();

        var firstRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/Cura/settings?id={settingId}");
        firstRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var firstResponse = await _httpClient.SendAsync(firstRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        // Act - retrieve again with the same user should still work
        var secondRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/Cura/settings?id={settingId}");
        secondRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var secondResponse = await _httpClient.SendAsync(secondRequest, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
    }

    [Fact]
    public async Task GetSettings_NotAuthenticated_ReturnsUnauthorized()
    {
        // Arrange
        var settingId = await SaveCuraSettingsAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Cura/settings?id={settingId}");

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetSettings_NonExistentGuid_ReturnsNotFound()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Cura/settings?id={Guid.NewGuid()}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion
}
