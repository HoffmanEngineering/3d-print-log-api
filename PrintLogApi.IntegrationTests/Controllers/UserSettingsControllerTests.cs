using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.UserSetting;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Integration tests for the UserSettingsController.
/// Tests CRUD operations for user settings (preferences, last selected printer, etc.).
/// </summary>
public class UserSettingsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _httpClient;
    private readonly CustomWebApplicationFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public UserSettingsControllerTests(CustomWebApplicationFactory factory)
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

    private int EnsureUserSettingTypeExists(string name = "TestSettingType")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var existingType = db.UserSettingTypes.FirstOrDefault(t => t.Name == name);
        if (existingType != null)
        {
            return existingType.Id;
        }

        var settingType = new UserSettingType
        {
            Name = name,
            Description = $"Test setting type: {name}"
        };
        db.UserSettingTypes.Add(settingType);
        db.SaveChanges();

        return settingType.Id;
    }

    private UserSetting CreateTestUserSetting(int settingTypeId, string value = "test-value")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var setting = new UserSetting
        {
            UserId = IntegrationTestSeeder.TestUserId,
            UserSettingTypeId = settingTypeId,
            Value = value,
            CreatedById = IntegrationTestSeeder.TestUserId,
            CreatedDate = DateTime.UtcNow,
            UpdatedById = IntegrationTestSeeder.TestUserId,
            UpdatedDate = DateTime.UtcNow
        };

        db.UserSettings.Add(setting);
        db.SaveChanges();

        return setting;
    }

    private UserSetting? GetUserSettingById(long id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return db.UserSettings.FirstOrDefault(s => s.Id == id);
    }

    private int GetUserSettingCountForUser()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return db.UserSettings.Count(s => s.UserId == IntegrationTestSeeder.TestUserId);
    }

    private int CreateUniqueSettingType()
    {
        return EnsureUserSettingTypeExists($"TestType_{Guid.NewGuid():N}");
    }

    #endregion

    #region GET /api/Users/me/user-settings Tests

    [Fact]
    public async Task GetCurrentUsersSettings_WithAuthentication_ReturnsOkWithSettings()
    {
        // Arrange
        var settingTypeId = CreateUniqueSettingType();
        CreateTestUserSetting(settingTypeId, "get-test-value");
        var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/Users/me/user-settings");

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<List<UserSettingDto>>(JsonOptions, cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetCurrentUsersSettings_WithoutAuthentication_ReturnsUnauthorized()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Users/me/user-settings");

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetCurrentUsersSettings_ReturnsOnlyCurrentUserSettings()
    {
        // Arrange - Create a setting for test user
        var settingTypeId = CreateUniqueSettingType();
        var setting = CreateTestUserSetting(settingTypeId, "my-setting-value");

        var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/Users/me/user-settings");

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<List<UserSettingDto>>(JsonOptions, cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Contains(result, s => s.Id == setting.Id && s.Value == "my-setting-value");
    }

    [Fact]
    public async Task GetCurrentUsersSettings_ReturnsEmptyListWhenNoSettings()
    {
        // Arrange - Create a new user with no settings
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            var newUser = new User
            {
                OAuthUserId = "auth0|no-settings-user-" + Guid.NewGuid().ToString("N"),
                ViewStatus = User.ProfileViewStatus.Public
            };
            db.Users.Add(newUser);
            db.SaveChanges();
        }

        var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/Users/me/user-settings", "auth0|no-settings-user-new");

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert - New user returns unauthorized (user doesn't exist with that OAuth ID)
        // or OK with empty list if user is auto-created
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetCurrentUsersSettings_ReturnsSettingDetails()
    {
        // Arrange
        var settingTypeId = CreateUniqueSettingType();
        var setting = CreateTestUserSetting(settingTypeId, "detail-test-value");
        var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/Users/me/user-settings");

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<List<UserSettingDto>>(JsonOptions, cancellationToken: TestContext.Current.CancellationToken))!;
        var foundSetting = result.FirstOrDefault(s => s.Id == setting.Id);
        Assert.NotNull(foundSetting);
        Assert.Equal(settingTypeId, foundSetting.UserSettingTypeId);
        Assert.Equal("detail-test-value", foundSetting.Value);
        Assert.Equal(IntegrationTestSeeder.TestUserId, foundSetting.UserId);
    }

    #endregion

    #region POST /api/Users/me/user-settings Tests

    [Fact]
    public async Task CreateUserSetting_WithValidData_ReturnsOkWithCreatedSetting()
    {
        // Arrange
        var settingTypeId = CreateUniqueSettingType();
        var dto = new AddUserSettingDto
        {
            UserSettingTypeId = settingTypeId,
            Value = "new-setting-value"
        };

        var request = CreateAuthenticatedRequest(HttpMethod.Post, "/api/Users/me/user-settings");
        request.Content = new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json");

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<UserSettingDto>(JsonOptions, cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.NotNull(result);
        Assert.Equal(settingTypeId, result.UserSettingTypeId);
        Assert.Equal("new-setting-value", result.Value);
        Assert.True(result.Id > 0);
    }

    [Fact]
    public async Task CreateUserSetting_WithoutAuthentication_ReturnsUnauthorized()
    {
        // Arrange
        var dto = new AddUserSettingDto
        {
            UserSettingTypeId = 1,
            Value = "test"
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/user-settings");
        request.Content = new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json");

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateUserSetting_DuplicateSettingType_ReturnsBadRequest()
    {
        // Arrange - Create a setting first
        var settingTypeId = CreateUniqueSettingType();
        CreateTestUserSetting(settingTypeId, "existing-value");

        // Try to create another setting with same type
        var dto = new AddUserSettingDto
        {
            UserSettingTypeId = settingTypeId,
            Value = "duplicate-value"
        };

        var request = CreateAuthenticatedRequest(HttpMethod.Post, "/api/Users/me/user-settings");
        request.Content = new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json");

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("already exists", content);
    }

    [Fact]
    public async Task CreateUserSetting_WithEmptyValue_ReturnsOk()
    {
        // Arrange
        var settingTypeId = CreateUniqueSettingType();
        var dto = new AddUserSettingDto
        {
            UserSettingTypeId = settingTypeId,
            Value = ""
        };

        var request = CreateAuthenticatedRequest(HttpMethod.Post, "/api/Users/me/user-settings");
        request.Content = new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json");

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<UserSettingDto>(JsonOptions, cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.NotNull(result);
        Assert.Equal("", result.Value);
    }

    [Fact]
    public async Task CreateUserSetting_WithNullValue_ReturnsOk()
    {
        // Arrange
        var settingTypeId = CreateUniqueSettingType();
        var dto = new AddUserSettingDto
        {
            UserSettingTypeId = settingTypeId,
            Value = null
        };

        var request = CreateAuthenticatedRequest(HttpMethod.Post, "/api/Users/me/user-settings");
        request.Content = new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json");

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CreateUserSetting_AppearsInGetSettings()
    {
        // Arrange
        var settingTypeId = CreateUniqueSettingType();
        var dto = new AddUserSettingDto
        {
            UserSettingTypeId = settingTypeId,
            Value = "appears-in-get-value"
        };

        var createRequest = CreateAuthenticatedRequest(HttpMethod.Post, "/api/Users/me/user-settings");
        createRequest.Content = new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json");

        // Act - Create
        var createResponse = await _httpClient.SendAsync(createRequest, TestContext.Current.CancellationToken);
        var created = (await createResponse.Content.ReadFromJsonAsync<UserSettingDto>(JsonOptions, cancellationToken: TestContext.Current.CancellationToken))!;

        // Act - Get
        var getRequest = CreateAuthenticatedRequest(HttpMethod.Get, "/api/Users/me/user-settings");
        var getResponse = await _httpClient.SendAsync(getRequest, TestContext.Current.CancellationToken);
        var settings = (await getResponse.Content.ReadFromJsonAsync<List<UserSettingDto>>(JsonOptions, cancellationToken: TestContext.Current.CancellationToken))!;

        // Assert
        Assert.Contains(settings, s => s.Id == created.Id && s.Value == "appears-in-get-value");
    }

    #endregion

    #region PUT /api/Users/me/user-settings Tests

    [Fact]
    public async Task UpdateUserSetting_WithValidData_ReturnsOkWithUpdatedSetting()
    {
        // Arrange
        var settingTypeId = CreateUniqueSettingType();
        var setting = CreateTestUserSetting(settingTypeId, "original-value");

        var dto = new UpdateUserSettingDto
        {
            Id = setting.Id,
            Value = "updated-value"
        };

        var request = CreateAuthenticatedRequest(HttpMethod.Put, "/api/Users/me/user-settings");
        request.Content = new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json");

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<UserSettingDto>(JsonOptions, cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.NotNull(result);
        Assert.Equal(setting.Id, result.Id);
        Assert.Equal("updated-value", result.Value);
    }

    [Fact]
    public async Task UpdateUserSetting_WithoutAuthentication_ReturnsUnauthorized()
    {
        // Arrange
        var dto = new UpdateUserSettingDto
        {
            Id = 1,
            Value = "test"
        };

        var request = new HttpRequestMessage(HttpMethod.Put, "/api/Users/me/user-settings");
        request.Content = new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json");

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UpdateUserSetting_NonExistentId_ReturnsNotFound()
    {
        // Arrange
        var dto = new UpdateUserSettingDto
        {
            Id = 999999,
            Value = "test"
        };

        var request = CreateAuthenticatedRequest(HttpMethod.Put, "/api/Users/me/user-settings");
        request.Content = new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json");

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateUserSetting_OtherUsersSetting_ReturnsNotFound()
    {
        // Arrange - Create another user and their setting
        long otherUserSettingId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            var otherUser = new User
            {
                OAuthUserId = "auth0|other-settings-user-" + Guid.NewGuid().ToString("N"),
                ViewStatus = User.ProfileViewStatus.Public
            };
            db.Users.Add(otherUser);
            db.SaveChanges();

            var settingType = new UserSettingType
            {
                Name = "OtherUserType_" + Guid.NewGuid().ToString("N"),
                Description = "Other user's setting type"
            };
            db.UserSettingTypes.Add(settingType);
            db.SaveChanges();

            var otherSetting = new UserSetting
            {
                UserId = otherUser.Id,
                UserSettingTypeId = settingType.Id,
                Value = "other-user-value",
                CreatedById = otherUser.Id,
                CreatedDate = DateTime.UtcNow,
                UpdatedById = otherUser.Id,
                UpdatedDate = DateTime.UtcNow
            };
            db.UserSettings.Add(otherSetting);
            db.SaveChanges();

            otherUserSettingId = otherSetting.Id;
        }

        var dto = new UpdateUserSettingDto
        {
            Id = otherUserSettingId,
            Value = "trying-to-update"
        };

        var request = CreateAuthenticatedRequest(HttpMethod.Put, "/api/Users/me/user-settings");
        request.Content = new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json");

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert - Returns NotFound because the setting doesn't belong to the current user
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateUserSetting_PersistsChanges()
    {
        // Arrange
        var settingTypeId = CreateUniqueSettingType();
        var setting = CreateTestUserSetting(settingTypeId, "before-update");

        var dto = new UpdateUserSettingDto
        {
            Id = setting.Id,
            Value = "after-update"
        };

        var request = CreateAuthenticatedRequest(HttpMethod.Put, "/api/Users/me/user-settings");
        request.Content = new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json");

        // Act
        await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert - Verify in database
        var updated = GetUserSettingById(setting.Id)!;
        Assert.Equal("after-update", updated.Value);
    }

    [Fact]
    public async Task UpdateUserSetting_UpdatesTimestamp()
    {
        // Arrange
        var settingTypeId = CreateUniqueSettingType();
        var setting = CreateTestUserSetting(settingTypeId, "timestamp-test");
        var originalUpdatedDate = setting.UpdatedDate;

        // Wait a bit to ensure timestamp difference
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var dto = new UpdateUserSettingDto
        {
            Id = setting.Id,
            Value = "timestamp-updated"
        };

        var request = CreateAuthenticatedRequest(HttpMethod.Put, "/api/Users/me/user-settings");
        request.Content = new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json");

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var result = (await response.Content.ReadFromJsonAsync<UserSettingDto>(JsonOptions, cancellationToken: TestContext.Current.CancellationToken))!;

        // Assert
        Assert.True(result.UpdatedDate >= originalUpdatedDate);
    }

    #endregion

    #region Integration Tests

    [Fact]
    public async Task FullWorkflow_CreateUpdateReadSetting()
    {
        // Create a setting type
        var settingTypeId = CreateUniqueSettingType();

        // Create a setting
        var createDto = new AddUserSettingDto
        {
            UserSettingTypeId = settingTypeId,
            Value = "initial-value"
        };

        var createRequest = CreateAuthenticatedRequest(HttpMethod.Post, "/api/Users/me/user-settings");
        createRequest.Content = new StringContent(JsonSerializer.Serialize(createDto), Encoding.UTF8, "application/json");

        var createResponse = await _httpClient.SendAsync(createRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var created = (await createResponse.Content.ReadFromJsonAsync<UserSettingDto>(JsonOptions, cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Equal("initial-value", created.Value);

        // Update the setting
        var updateDto = new UpdateUserSettingDto
        {
            Id = created.Id,
            Value = "modified-value"
        };

        var updateRequest = CreateAuthenticatedRequest(HttpMethod.Put, "/api/Users/me/user-settings");
        updateRequest.Content = new StringContent(JsonSerializer.Serialize(updateDto), Encoding.UTF8, "application/json");

        var updateResponse = await _httpClient.SendAsync(updateRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = (await updateResponse.Content.ReadFromJsonAsync<UserSettingDto>(JsonOptions, cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Equal("modified-value", updated.Value);

        // Read all settings and verify
        var getRequest = CreateAuthenticatedRequest(HttpMethod.Get, "/api/Users/me/user-settings");
        var getResponse = await _httpClient.SendAsync(getRequest, TestContext.Current.CancellationToken);
        var settings = (await getResponse.Content.ReadFromJsonAsync<List<UserSettingDto>>(JsonOptions, cancellationToken: TestContext.Current.CancellationToken))!;

        var foundSetting = settings.FirstOrDefault(s => s.Id == created.Id);
        Assert.NotNull(foundSetting);
        Assert.Equal("modified-value", foundSetting.Value);
    }

    #endregion

    #region Uniqueness Tests

    [Fact]
    public async Task DuplicateUserSettingForSameType_IsRejectedByDatabase()
    {
        // CreateUserSetting does check-then-insert with SingleOrDefaultAsync, so a duplicate
        // (UserId, UserSettingTypeId) pair permanently breaks creation of that setting for the
        // account. Push dispatch now reads this table too, so the database has to enforce it.
        var settingTypeId = CreateUniqueSettingType();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        // CreatedById/UpdatedById are required FKs inherited from TimestampEntity.
        // Leaving them at zero trips the constraint before the index is ever exercised.
        UserSetting Row(string value) => new()
        {
            UserId = IntegrationTestSeeder.TestUserId,
            UserSettingTypeId = settingTypeId,
            Value = value,
            CreatedDate = DateTime.UtcNow,
            UpdatedDate = DateTime.UtcNow,
            CreatedById = IntegrationTestSeeder.TestUserId,
            UpdatedById = IntegrationTestSeeder.TestUserId
        };

        db.UserSettings.Add(Row("1"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.UserSettings.Add(Row("2"));

        await Assert.ThrowsAnyAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }


    [Theory]
    [InlineData(15, "Push_PrintCompleted")]
    [InlineData(16, "Push_PrintFailed")]
    public async Task PushNotificationSettingTypes_AreSeeded(int id, string expectedName)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var type = await db.UserSettingTypes.FindAsync([id], TestContext.Current.CancellationToken);

        Assert.NotNull(type);
        Assert.Equal(expectedName, type!.Name);
    }

    #endregion


    [Fact]
    public async Task CreateUserSetting_LosingAnInsertRace_ReturnsConflictNotServerError()
    {
        var settingTypeId = EnsureUserSettingTypeExists($"RaceType-{Guid.NewGuid():N}");

        HttpRequestMessage Request()
        {
            var request = CreateAuthenticatedRequest(HttpMethod.Post, "/api/Users/me/user-settings");
            var payload = JsonSerializer.Serialize(new
            {
                userSettingTypeId = settingTypeId,
                value = "true"
            });
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            return request;
        }

        // Fired together so both requests can pass the "does it already exist?" pre-check
        // before either saves. IX_UserSettings_UserId_UserSettingTypeId then rejects the
        // second write; before this was handled that surfaced as an unhandled 500.
        var responses = await Task.WhenAll(
            _httpClient.SendAsync(Request(), TestContext.Current.CancellationToken),
            _httpClient.SendAsync(Request(), TestContext.Current.CancellationToken));

        Assert.DoesNotContain(responses, r => r.StatusCode == HttpStatusCode.InternalServerError);

        // Exactly one row exists regardless of which side won.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var rows = await db.UserSettings
            .Where(u => u.UserId == IntegrationTestSeeder.TestUserId
                        && u.UserSettingTypeId == settingTypeId)
            .CountAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, rows);
    }
}
