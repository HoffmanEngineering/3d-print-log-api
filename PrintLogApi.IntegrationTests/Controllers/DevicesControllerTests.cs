using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
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
}
