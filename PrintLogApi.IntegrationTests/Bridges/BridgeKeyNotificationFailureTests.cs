using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PrintLogApi.Models;
using Xunit;
using static PrintLogApi.IntegrationTests.Bridges.BridgeTestKit;

namespace PrintLogApi.IntegrationTests.Bridges;

/// <summary>
/// The pending-key notice is sent after the check-in commits, and best effort: a failure there
/// must not turn a committed check-in into a 500 (story 14a review).
/// </summary>
public class BridgeKeyNotificationFailureTests(BridgeKeyNotificationFailureTests.Factory factory) : IClassFixture<BridgeKeyNotificationFailureTests.Factory>
{
    public class Factory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.ConfigureDbContext<PrintLogContext>(options => options.AddInterceptors(new FailingNotificationInterceptor())));
        }
    }

    /// <summary>Fails every save that adds a notification.</summary>
    public sealed class FailingNotificationInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => eventData.Context!.ChangeTracker.Entries<Notification>().Any(e => e.State == EntityState.Added)
                ? throw new InvalidOperationException("notification store down")
                : ValueTask.FromResult(result);
    }

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task ANotificationFailure_LeavesTheCheckInCommitted()
    {
        var bridge = await PairBridgeAsync(_client, "notifyfail");
        using var key = new BridgeSealedCredentialTests.BridgeKey();
        using var rotated = new BridgeSealedCredentialTests.BridgeKey();
        await CheckInOkAsync(_client, bridge.Token, new { publicKey = key.PublicKey });

        var checkIn = await CheckInOkAsync(_client, bridge.Token, new { publicKey = rotated.PublicKey });

        Assert.Equal(rotated.Fingerprint, checkIn.GetProperty("pendingPublicKeyFingerprint").GetString());
        var dto = await GetAsUserAsync(_client, bridge.Subject, $"/api/bridges/{bridge.GrantId}");
        Assert.Equal(rotated.PublicKey, dto.GetProperty("pendingPublicKey").GetString());
    }
}
