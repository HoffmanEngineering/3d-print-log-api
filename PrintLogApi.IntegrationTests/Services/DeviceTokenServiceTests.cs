using PrintLogApi.Models;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

public class DeviceTokenServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public DeviceTokenServiceTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task RegisterDevice_IsIdempotent()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IDeviceTokenService>();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var token = $"tok-{Guid.NewGuid():N}";

        await service.RegisterDevice(IntegrationTestSeeder.TestUserId, token, DevicePlatform.Android, "1.3.0");
        await service.RegisterDevice(IntegrationTestSeeder.TestUserId, token, DevicePlatform.Android, "1.3.0");

        Assert.Single(db.DeviceTokens.Where(d => d.Token == token));
    }

    [Fact]
    public async Task RegisterDevice_ReassignsTokenToNewOwner()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IDeviceTokenService>();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var token = $"tok-{Guid.NewGuid():N}";

        await service.RegisterDevice(IntegrationTestSeeder.TestUserId, token, DevicePlatform.Android, "1.3.0");
        await service.RegisterDevice(IntegrationTestSeeder.SecondaryUserId, token, DevicePlatform.Android, "1.3.0");

        var rows = db.DeviceTokens.Where(d => d.Token == token).ToList();
        Assert.Single(rows);
        Assert.Equal(IntegrationTestSeeder.SecondaryUserId, rows[0].UserId);
    }

    [Fact]
    public async Task RegisterDevice_SurvivesConcurrentFirstRegistration()
    {
        var token = $"tok-{Guid.NewGuid():N}";

        // Two independent scopes, so neither sees the other's change tracker. Both observe
        // "no existing row" and both try to insert; the unique index lets exactly one win.
        // Without conflict handling the loser surfaces as a 500 on a routine app launch.
        async Task Register()
        {
            using var scope = _factory.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IDeviceTokenService>();
            await service.RegisterDevice(IntegrationTestSeeder.TestUserId, token, DevicePlatform.Android, "1.3.0");
        }

        await Task.WhenAll(Register(), Register());

        using var verifyScope = _factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<PrintLogContext>();
        Assert.Single(db.DeviceTokens.Where(d => d.Token == token));
    }

    [Fact]
    public async Task GetTokensForUser_ReturnsAllDevices()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IDeviceTokenService>();
        var a = $"tok-{Guid.NewGuid():N}";
        var b = $"tok-{Guid.NewGuid():N}";

        await service.RegisterDevice(IntegrationTestSeeder.TestUserId, a, DevicePlatform.Android, null);
        await service.RegisterDevice(IntegrationTestSeeder.TestUserId, b, DevicePlatform.Android, null);

        var tokens = await service.GetTokensForUser(IntegrationTestSeeder.TestUserId);

        Assert.Contains(a, tokens);
        Assert.Contains(b, tokens);
    }

    [Fact]
    public async Task PruneTokens_DeletesOnlyNamedTokens()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IDeviceTokenService>();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var doomed = $"tok-{Guid.NewGuid():N}";
        var kept = $"tok-{Guid.NewGuid():N}";

        await service.RegisterDevice(IntegrationTestSeeder.TestUserId, doomed, DevicePlatform.Android, null);
        await service.RegisterDevice(IntegrationTestSeeder.TestUserId, kept, DevicePlatform.Android, null);

        await service.PruneTokens([doomed]);

        Assert.Empty(db.DeviceTokens.Where(d => d.Token == doomed));
        Assert.Single(db.DeviceTokens.Where(d => d.Token == kept));
    }

    [Fact]
    public async Task RemoveDevice_OnlyRemovesCallersOwnToken()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IDeviceTokenService>();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var token = $"tok-{Guid.NewGuid():N}";
        await service.RegisterDevice(IntegrationTestSeeder.TestUserId, token, DevicePlatform.Android, null);

        var removed = await service.RemoveDevice(IntegrationTestSeeder.SecondaryUserId, token);

        Assert.False(removed);
        Assert.Single(db.DeviceTokens.Where(d => d.Token == token));
    }
}
