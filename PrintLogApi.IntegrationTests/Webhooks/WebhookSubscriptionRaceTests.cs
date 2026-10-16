using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Webhook;
using PrintLogApi.Webhooks;
using Xunit;

namespace PrintLogApi.IntegrationTests.Webhooks;

/// <summary>
/// What can change between a webhook write's read and its save (#235): a parallel create that
/// takes the last slot, and an auto-disable landing while the user turns the webhook back on.
/// Built deterministically, as in <c>ConnectionServiceRaceTests</c>: an interceptor commits the
/// competing write from a second context just before the first save.
/// There is deliberately no test that fires parallel HTTP requests: every host here shares one
/// in-memory SQLite connection, which is not thread-safe, so such a test fails on the connection
/// rather than on the code under test.
/// </summary>
public class WebhookSubscriptionRaceTests(WebhookTestFactory factory) : IClassFixture<WebhookTestFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string HookUrl() => $"https://hooks.example.test/api/webhook/{Guid.NewGuid():N}";

    private static WebhookSubscription Row(long userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Url = HookUrl(),
        Events = WebhookEvents.All,
        Enabled = true,
        EncryptedSecret = "v1.00000000.AAAA",
        CreatedDate = DateTime.UtcNow,
        UpdatedDate = DateTime.UtcNow,
    };

    [Fact]
    public async Task Create_LosingTheLastSlotToAParallelCreate_IsTheLimit_AndLeavesOneRow()
    {
        var user = await factory.CreateUserAsync();
        using var scope = factory.Services.CreateScope();
        var shared = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var competitor = new BeforeFirstSave(shared.Database.GetDbConnection(), other =>
        {
            other.WebhookSubscriptions.Add(Row(user.Id));
            return other.SaveChangesAsync(Ct);
        });
        var service = RacingService(scope, shared, competitor, out var racing);
        await using var _ = racing;

        await Assert.ThrowsAsync<WebhookLimitException>(() =>
            service.CreateAsync(user.Id, new SaveWebhookSubscriptionDto { Url = HookUrl() }, Ct));

        Assert.True(competitor.Fired, "the competing create never ran, so no race was tested");
        Assert.Equal(1, await shared.WebhookSubscriptions.CountAsync(w => w.UserId == user.Id, Ct));
    }

    [Fact]
    public async Task TurningOn_WinsOverAnAutoDisableThatLandedAfterTheRead()
    {
        var user = await factory.CreateUserAsync();
        using var scope = factory.Services.CreateScope();
        var shared = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var row = Row(user.Id);
        shared.WebhookSubscriptions.Add(row);
        await shared.SaveChangesAsync(Ct);
        shared.ChangeTracker.Clear();

        var competitor = new BeforeFirstSave(shared.Database.GetDbConnection(), other =>
            other.WebhookSubscriptions.Where(w => w.Id == row.Id).ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Enabled, false)
                .SetProperty(w => w.ConsecutiveFailures, 10)
                .SetProperty(w => w.AutoDisabledAt, DateTime.UtcNow), Ct));
        var service = RacingService(scope, shared, competitor, out var racing);
        await using var _ = racing;

        await service.UpdateAsync(user.Id, row.Id, new SaveWebhookSubscriptionDto { Url = row.Url, Enabled = true }, Ct);

        Assert.True(competitor.Fired, "the auto-disable never ran, so no race was tested");
        var stored = await shared.WebhookSubscriptions.AsNoTracking().SingleAsync(w => w.Id == row.Id, Ct);
        Assert.True(stored.Enabled);
        Assert.Equal(0, stored.ConsecutiveFailures);
        Assert.Null(stored.AutoDisabledAt);
    }

    [Fact]
    public async Task TurningOff_KeepsAFailureCountThatLandedAfterTheRead()
    {
        var user = await factory.CreateUserAsync();
        using var scope = factory.Services.CreateScope();
        var shared = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var row = Row(user.Id);
        shared.WebhookSubscriptions.Add(row);
        await shared.SaveChangesAsync(Ct);
        shared.ChangeTracker.Clear();

        var failedAt = DateTime.UtcNow;
        var competitor = new BeforeFirstSave(shared.Database.GetDbConnection(), other =>
            other.WebhookSubscriptions.Where(w => w.Id == row.Id).ExecuteUpdateAsync(s => s
                .SetProperty(w => w.ConsecutiveFailures, 2)
                .SetProperty(w => w.LastDeliveryAt, failedAt), Ct));
        var service = RacingService(scope, shared, competitor, out var racing);
        await using var _ = racing;

        await service.UpdateAsync(user.Id, row.Id, new SaveWebhookSubscriptionDto { Url = row.Url, Enabled = false }, Ct);

        Assert.True(competitor.Fired, "the failure never landed, so no race was tested");
        var stored = await shared.WebhookSubscriptions.AsNoTracking().SingleAsync(w => w.Id == row.Id, Ct);
        Assert.False(stored.Enabled);
        Assert.Equal(2, stored.ConsecutiveFailures);
    }

    [Fact]
    public async Task Toggling_WithTheUrlUnchanged_DoesNotRecheckDns()
    {
        var user = await factory.CreateUserAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var row = Row(user.Id);
        row.Url = "https://moved.example.test/api/webhook/x";
        db.WebhookSubscriptions.Add(row);
        await db.SaveChangesAsync(Ct);
        // The host no longer resolves; delivery would refuse it at connect time anyway.
        factory.Resolver.Map("moved.example.test");

        var updated = await scope.ServiceProvider.GetRequiredService<IWebhookSubscriptionService>()
            .UpdateAsync(user.Id, row.Id, new SaveWebhookSubscriptionDto { Url = row.Url, Enabled = false }, Ct);

        Assert.False(updated!.Enabled);
    }

    private static WebhookSubscriptionService RacingService(
        IServiceScope scope, PrintLogContext shared, BeforeFirstSave competitor, out PrintLogContext racingContext)
    {
        var options = new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlite(shared.Database.GetDbConnection())
            .AddInterceptors(competitor)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        racingContext = new PrintLogContext(options);
        return ActivatorUtilities.CreateInstance<WebhookSubscriptionService>(scope.ServiceProvider, racingContext);
    }

    private sealed class BeforeFirstSave(System.Data.Common.DbConnection connection, Func<PrintLogContext, Task> write)
        : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired)
            {
                Fired = true;
                var options = new DbContextOptionsBuilder<PrintLogContext>()
                    .UseSqlite(connection)
                    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                    .Options;
                await using var other = new PrintLogContext(options);
                await write(other);
            }
            return result;
        }
    }
}
