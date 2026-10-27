using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PrintLogApi.Authentication.Devices;
using PrintLogApi.Services.DeviceGrants;
using Xunit;

namespace PrintLogApi.IntegrationTests.Devices;

/// <summary>
/// A re-pair racing a revoke (#286 review): the user deletes the grant after the re-pair read it
/// and before its save, so the rotation updates no row. That must answer
/// <see cref="DeviceGrantRevokedException"/> (409 <c>device_grant_revoked</c>), never a 500, and
/// must spend the pairing token: an approval given before the revoke may not bring the device back.
/// </summary>
public class DeviceRepairRaceTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    /// <summary>Deletes one grant, with raw SQL, the moment the context it is attached to starts a save.</summary>
    private sealed class RevokeBeforeSave(long grantId) : SaveChangesInterceptor
    {
        private bool _armed = true;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_armed)
            {
                _armed = false;
                await eventData.Context!.Database.ExecuteSqlRawAsync(
                    "DELETE FROM DeviceGrants WHERE Id = {0}", [grantId], cancellationToken);
            }

            return result;
        }
    }

    private static PairingTokenSpend Spend(string label) =>
        new(DeviceTokenFormat.Hash("jti:" + label + Guid.NewGuid()), DateTime.UtcNow.AddMinutes(15));

    [Fact]
    public async Task ARevokeBetweenTheReadAndTheSave_IsDeviceGrantRevoked_AndSpendsTheToken()
    {
        using var scope = factory.Services.CreateScope();
        var shared = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var options = Options.Create(new DevicePairingOptions { MaxGrantsPerUser = 25 });
        var cache = scope.ServiceProvider.GetRequiredService<IMemoryCache>();
        var userId = IntegrationTestSeeder.TestUserId;
        var deviceId = Guid.NewGuid();

        var first = await new DeviceGrantService(shared, cache, options, TimeProvider.System)
            .PairAsync(userId, deviceId, "bridge", [DeviceScopes.PrintsRead], "client", Spend("first"), TestContext.Current.CancellationToken);

        var racing = new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlite(shared.Database.GetDbConnection())
            .AddInterceptors(new RevokeBeforeSave(first.Grant.Id))
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new PrintLogContext(racing);
        var service = new DeviceGrantService(context, cache, options, TimeProvider.System);
        var repair = Spend("repair");

        await Assert.ThrowsAsync<DeviceGrantRevokedException>(() =>
            service.PairAsync(userId, deviceId, "bridge", [DeviceScopes.PrintsRead], "client", repair, TestContext.Current.CancellationToken));

        await using var check = new PrintLogContext(new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlite(shared.Database.GetDbConnection()).Options);
        Assert.False(await check.DeviceGrants.AnyAsync(g => g.UserId == userId && g.DeviceId == deviceId, TestContext.Current.CancellationToken));
        Assert.True(await check.PairingTokenUses.AnyAsync(u => u.TokenKeyHash == repair.KeyHash, TestContext.Current.CancellationToken));

        // So the same approval cannot pair the device back afterwards.
        await Assert.ThrowsAsync<PairingTokenUsedException>(() =>
            new DeviceGrantService(check, cache, options, TimeProvider.System)
                .PairAsync(userId, deviceId, "bridge", [DeviceScopes.PrintsRead], "client", repair, TestContext.Current.CancellationToken));
    }
}
