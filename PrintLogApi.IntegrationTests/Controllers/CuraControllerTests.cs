using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.CuraSettings;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

public class CuraControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _httpClient;
    private readonly CustomWebApplicationFactory _factory;

    public CuraControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
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

    /// <summary>
    /// The route is anonymous and reads the whole body, so it is capped at the same 4 MiB as
    /// <c>slicer-settings</c>. Real clients stay far below it: the Slic3r uploader reads at most a
    /// 2,000,000-character window of the G-code, and the Cura plugin's snapshot is 300x300. TestServer
    /// does not enforce <c>IHttpMaxRequestBodySizeFeature</c>, so this pins the metadata Kestrel reads.
    /// </summary>
    [Fact]
    public void SaveSettings_LimitsItsBodyToFourMebibytes()
    {
        var endpoint = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "api/Cura/settings"
                && e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("POST"));

        var limit = endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>();

        Assert.NotNull(limit);
        Assert.Equal(4 * 1024 * 1024, limit.MaxRequestBodySize);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    [InlineData("\"settings\"")]
    [InlineData("42")]
    [InlineData("{\"curaVersion\":")]
    public async Task SaveSettings_BodyThatIsNotAnObject_ReturnsBadRequest(string body)
    {
        var response = await PostRawAsync(Encoding.UTF8.GetBytes(body), "application/json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("curaVersion", 101)]
    [InlineData("pluginVersion", 101)]
    [InlineData("slicer", 51)]
    public async Task SaveSettings_FieldLongerThanItsColumn_ReturnsBadRequest(string field, int length)
    {
        var body = new Dictionary<string, object>
        {
            ["curaVersion"] = "5.4.0",
            ["pluginVersion"] = "1.0.0",
            ["settings"] = new { note = "n" },
        };
        body[field] = new string('v', length);

        var response = await PostRawAsync(JsonSerializer.SerializeToUtf8Bytes(body), "application/json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("curaVersion", 100)]
    [InlineData("pluginVersion", 100)]
    [InlineData("slicer", 50)]
    public async Task SaveSettings_FieldAtItsColumnLength_IsAccepted(string field, int length)
    {
        var body = new Dictionary<string, object>
        {
            ["curaVersion"] = "5.4.0",
            ["settings"] = new { note = "n" },
        };
        body[field] = new string('v', length);

        var response = await PostRawAsync(JsonSerializer.SerializeToUtf8Bytes(body), "application/json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The Cura plugin posts <c>json.dumps(data).encode("utf-8")</c> through Cura's
    /// HttpRequestManager: Python's separators, non-ASCII escaped, and no Content-Type header. The
    /// settings object must be stored as the exact text that was sent, and the response must stay
    /// <c>{"newSettingId": ...}</c>, because the plugin reads it.
    /// </summary>
    [Fact]
    public async Task SaveSettings_CuraPluginPayload_StoresTheSettingsTextUnchanged()
    {
        const string body =
            "{\"curaVersion\": \"5.6.0\", \"pluginVersion\": \"1.4.1\", \"settings\": {\"note\": \"Layer Height: 0.2\\nInfill: 20%\\nNozzle: 0.4 \\u00b7 Brass\", "
            + "\"print_name\": \"CE3E3V2_benchy\", \"cura_version\": \"5.6.0\", \"print_time\": 4321, \"material_used_mg\": 12345, "
            + "\"machine_name\": \"Creality Ender-3 V2\", \"snapshot\": \"iVBORw0KGgoAAAANSUhEUgAAASwAAAEsCAYAAAB5fY51AAAA\"}}";

        var response = await PostRawAsync(Encoding.UTF8.GetBytes(body), contentType: null);

        await AssertStoredAsIsAsync(response, body, expectedSlicer: null);
    }

    /// <summary>
    /// The Slic3r post-processing uploader posts its DTO with System.Text.Json, indented and
    /// camel-cased, with a <c>slicer</c> field and the settings object (note, base64 thumbnail,
    /// filament usage). Same guarantees as the Cura payload.
    /// </summary>
    [Fact]
    public async Task SaveSettings_Slic3rUploaderPayload_StoresTheSettingsTextUnchanged()
    {
        var snapshot = Convert.ToBase64String(Enumerable.Range(0, 96_000).Select(i => (byte)i).ToArray());
        var body = JsonSerializer.Serialize(new
        {
            slicer = "OrcaSlicer",
            curaVersion = "2.2.0",
            pluginVersion = "1.1.2.0",
            settings = new
            {
                note = "--- Slicer settings (OrcaSlicer 2.2.0) ---\nQuality:\n  Layer Height: 0.2\n  Sparse Infill Density: 15%",
                print_name = "Calibration Cube",
                estimated_print_time_seconds = 996,
                material_used_mg = (int?)null,
                snapshot,
                file_name = "orcaslicer-2.2.0-rc-calibration-cube.gcode",
                filamentUsage = new[]
                {
                    new
                    {
                        id = (long?)null,
                        filament = new { id = Guid.Empty, displayName = "Other" },
                        estimatedLengthInM = 1.488,
                        source = "Length",
                        estimatedSource = "Length",
                        notes = "Slot 1 · Green (#00AE42) · PLA",
                    },
                },
            },
        }, new JsonSerializerOptions { WriteIndented = true });

        var response = await PostRawAsync(Encoding.UTF8.GetBytes(body), "application/json; charset=utf-8");

        await AssertStoredAsIsAsync(response, body, expectedSlicer: "OrcaSlicer");
    }

    private async Task<HttpResponseMessage> PostRawAsync(byte[] body, string? contentType)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Cura/settings")
        {
            Content = new ByteArrayContent(body),
        };
        if (contentType is not null)
        {
            request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        }

        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task AssertStoredAsIsAsync(HttpResponseMessage response, string body, string? expectedSlicer)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["newSettingId"], result.RootElement.EnumerateObject().Select(p => p.Name));
        var id = result.RootElement.GetProperty("newSettingId").GetGuid();

        using var sent = JsonDocument.Parse(body);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var stored = db.CuraSettings.AsNoTracking().Single(c => c.Id == id);

        Assert.Equal(sent.RootElement.GetProperty("curaVersion").GetString(), stored.CuraVersion);
        Assert.Equal(sent.RootElement.GetProperty("pluginVersion").GetString(), stored.PluginVersion);
        Assert.Equal(expectedSlicer, stored.Slicer);
        Assert.Equal(sent.RootElement.GetProperty("settings").GetRawText(), stored._Settings);
        Assert.Null(stored.UserId);
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
    public async Task GetSettings_ResponseShape_IsWhatThePluginsRead()
    {
        // #164 linked connector settings to their print. That link is server-side only: the Cura
        // plugin's and the uploader's response must not grow a printId or a print.
        var settingId = await SaveCuraSettingsAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Cura/settings?id={settingId}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            ["createdDate", "curaVersion", "id", "pluginVersion", "settings", "slicer", "userId"],
            body.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
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
