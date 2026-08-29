using System.Net;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Integration tests for device (push token) registration.
/// </summary>
public class DevicesControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _httpClient;
    private readonly CustomWebApplicationFactory _factory;

    public DevicesControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _httpClient = factory.CreateClient();
    }

    [Fact]
    public async Task DeviceTokenTable_EnforcesUniqueToken()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var token = $"tok-{Guid.NewGuid():N}";

        DeviceToken Row() => new()
        {
            UserId = IntegrationTestSeeder.TestUserId,
            Token = token,
            Platform = DevicePlatform.Android,
            CreatedDate = DateTime.UtcNow,
            LastSeenDate = DateTime.UtcNow
        };

        db.DeviceTokens.Add(Row());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.DeviceTokens.Add(Row());

        await Assert.ThrowsAnyAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    // 0 is default(DevicePlatform) and so indistinguishable from "not supplied"; 99 is simply
    // undefined. [Required] alone accepts both on a non-nullable enum, which is why the DTO
    // carries [EnumDataType].
    [InlineData(0)]
    [InlineData(99)]
    public async Task RegisterDevice_RejectsUndefinedPlatform(int platform)
    {
        var request = AuthedRequest(HttpMethod.Post, "/api/Devices");
        request.Content = JsonContent.Create(new { token = $"tok-{Guid.NewGuid():N}", platform });

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RegisterDevice_RejectsMissingToken()
    {
        var request = AuthedRequest(HttpMethod.Post, "/api/Devices");
        request.Content = JsonContent.Create(new { platform = (int)DevicePlatform.Android });

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private HttpRequestMessage AuthedRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        return request;
    }

    /// <summary>
    /// Mints a real API key through the production service, so the key presented here is
    /// hashed and looked up by exactly the routine ApiKeyMiddleware uses. Re-implementing the
    /// hash in the seeder would let the two drift and the rejection test would prove nothing.
    /// </summary>
    private async Task<string> CreateRealApiKey()
    {
        using var scope = _factory.Services.CreateScope();
        var keys = scope.ServiceProvider.GetRequiredService<IUserApiKeyService>();
        var created = await keys.GenerateNewApiKey(IntegrationTestSeeder.TestUserId, "devices-controller-test");
        return created.PublicKey!;
    }

    [Fact]
    public async Task RegisterDevice_ReturnsNoContent_AndPersistsToken()
    {
        var token = $"tok-{Guid.NewGuid():N}";
        var request = AuthedRequest(HttpMethod.Post, "/api/devices");
        request.Content = JsonContent.Create(new { token, platform = 1, appVersion = "1.3.0" });

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        Assert.Single(db.DeviceTokens.Where(d => d.Token == token));
    }

    [Fact]
    public async Task RegisterDevice_RejectsApiKeyPrincipal()
    {
        var apiKey = await CreateRealApiKey();
        var token = $"tok-{Guid.NewGuid():N}";
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/devices");
        request.Headers.Add("X-Api-Key", apiKey);
        request.Content = JsonContent.Create(new { token, platform = 1, appVersion = "1.3.0" });

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // 401, not 403: pinning the scheme makes authorization re-authenticate with the
        // interactive scheme, which discards the GenericPrincipal ApiKeyMiddleware built, so
        // the request arrives anonymous and is challenged rather than forbidden. Either way
        // the API key never reaches the handler.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        Assert.Empty(db.DeviceTokens.Where(d => d.Token == token));
    }

    [Fact]
    public async Task RegisterDevice_RejectsApiKeyInQueryString()
    {
        var apiKey = await CreateRealApiKey();
        var token = $"tok-{Guid.NewGuid():N}";
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/devices?api_key={apiKey}");
        request.Content = JsonContent.Create(new { token, platform = 1, appVersion = "1.3.0" });

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // 401, not 403: pinning the scheme makes authorization re-authenticate with the
        // interactive scheme, which discards the GenericPrincipal ApiKeyMiddleware built, so
        // the request arrives anonymous and is challenged rather than forbidden. Either way
        // the API key never reaches the handler.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        Assert.Empty(db.DeviceTokens.Where(d => d.Token == token));
    }

    [Fact]
    public async Task DeleteDevice_RemovesOnlyCallersToken()
    {
        var token = $"tok-{Guid.NewGuid():N}";
        var post = AuthedRequest(HttpMethod.Post, "/api/devices");
        post.Content = JsonContent.Create(new { token, platform = 1, appVersion = "1.3.0" });
        await _httpClient.SendAsync(post, TestContext.Current.CancellationToken);

        var response = await _httpClient.SendAsync(
            AuthedRequest(HttpMethod.Delete, $"/api/devices/{token}"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        Assert.Empty(db.DeviceTokens.Where(d => d.Token == token));
    }
}
