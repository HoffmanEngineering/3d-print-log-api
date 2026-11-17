using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using PrintLogApi.Email;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email;

public class EmailVerificationRefresherTests : IClassFixture<CustomWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly CustomWebApplicationFactory _factory;

    public EmailVerificationRefresherTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        // The stub is a singleton shared by every test in the class, so reset every knob.
        _factory.Auth0.EmailStatus = null;
        _factory.Auth0.ThrowOnEmailStatus = false;
        _factory.Auth0.EmailStatusCalls = 0;
        _factory.Auth0.OnEmailStatus = null;
    }

    // A fresh user per test: the throttle flag lives in the process-wide IMemoryCache.
    private async Task<(long Id, string OAuthId)> SeedAsync(bool verified)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db, verified: verified);
        return (user.Id, user.OAuthUserId!);
    }

    private async Task<bool> RefreshAsync(long userId, bool force = false)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IEmailVerificationRefresher>().RefreshAsync(userId, Ct, force);
    }

    [Fact]
    public async Task AlreadyVerified_NoAuth0Call()
    {
        var (id, _) = await SeedAsync(verified: true);

        Assert.True(await RefreshAsync(id));
        Assert.Equal(0, _factory.Auth0.EmailStatusCalls);
    }

    [Fact]
    public async Task Auth0Verified_UpdatesUserAndReturnsTrue()
    {
        var (id, _) = await SeedAsync(verified: false);
        _factory.Auth0.EmailStatus = new Auth0EmailStatus("  New.Address@Example.com ", true);

        Assert.True(await RefreshAsync(id));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == id, Ct);
        Assert.True(user.EmailVerified);
        Assert.Equal("New.Address@Example.com", user.Email);
        Assert.NotNull(user.EmailUpdatedAt);
    }

    [Fact]
    public async Task Auth0Unverified_ReturnsFalse()
    {
        var (id, _) = await SeedAsync(verified: false);
        _factory.Auth0.EmailStatus = new Auth0EmailStatus("a@example.com", false);

        Assert.False(await RefreshAsync(id));
        Assert.Equal(1, _factory.Auth0.EmailStatusCalls);
    }

    [Fact]
    public async Task SecondCallWithinHour_IsThrottled()
    {
        var (id, _) = await SeedAsync(verified: false);
        _factory.Auth0.EmailStatus = new Auth0EmailStatus("a@example.com", false);

        Assert.False(await RefreshAsync(id));
        Assert.False(await RefreshAsync(id));

        Assert.Equal(1, _factory.Auth0.EmailStatusCalls);
    }

    // The dispatcher and resend have their own limits, so they skip the throttle check. The call
    // still sets the throttle, so the throttled GET right after it does not ask Auth0 again.
    [Fact]
    public async Task Force_BypassesTheThrottleAndStillSetsIt()
    {
        var (id, _) = await SeedAsync(verified: false);
        _factory.Auth0.EmailStatus = new Auth0EmailStatus("a@example.com", false);

        Assert.False(await RefreshAsync(id));
        Assert.False(await RefreshAsync(id, force: true));
        Assert.Equal(2, _factory.Auth0.EmailStatusCalls);

        Assert.False(await RefreshAsync(id));
        Assert.Equal(2, _factory.Auth0.EmailStatusCalls);
    }

    [Fact]
    public async Task Force_SetsTheThrottleWhenNoneExists()
    {
        var (id, _) = await SeedAsync(verified: false);
        _factory.Auth0.EmailStatus = new Auth0EmailStatus("a@example.com", false);

        Assert.False(await RefreshAsync(id, force: true));
        Assert.False(await RefreshAsync(id));

        Assert.Equal(1, _factory.Auth0.EmailStatusCalls);
    }

    [Fact]
    public async Task Auth0Throws_ReturnsFalseAndDoesNotThrow()
    {
        var (id, _) = await SeedAsync(verified: false);
        _factory.Auth0.ThrowOnEmailStatus = true;

        Assert.False(await RefreshAsync(id));

        // The throttle is set even though Auth0 failed, so a failing Auth0 is not hammered.
        Assert.False(await RefreshAsync(id));
        Assert.Equal(1, _factory.Auth0.EmailStatusCalls);
    }

    // HttpClient reports its own timeout as a TaskCanceledException while the caller's token is
    // still live. That is an Auth0 failure, not the request going away.
    [Fact]
    public async Task Auth0TimesOut_ReturnsFalseAndDoesNotThrow()
    {
        var (id, _) = await SeedAsync(verified: false);
        _factory.Auth0.OnEmailStatus = () => throw new TaskCanceledException("Simulated HttpClient timeout.");

        Assert.False(await RefreshAsync(id));
    }

    // A canceled request never learned anything, so it must not leave the throttle behind.
    [Fact]
    public async Task CanceledDuringAuth0Call_RemovesTheThrottle()
    {
        var (id, _) = await SeedAsync(verified: false);
        _factory.Auth0.EmailStatus = new Auth0EmailStatus("a@example.com", false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _factory.Auth0.OnEmailStatus = () =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
        };

        using (var scope = _factory.Services.CreateScope())
        {
            var refresher = scope.ServiceProvider.GetRequiredService<IEmailVerificationRefresher>();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresher.RefreshAsync(id, cts.Token));
        }

        _factory.Auth0.OnEmailStatus = null;
        Assert.False(await RefreshAsync(id));
        Assert.Equal(2, _factory.Auth0.EmailStatusCalls);
    }

    // The dispatcher shares one context across a whole tick. A failed save must not leave the
    // user Modified there, or the dispatcher's next SaveChanges retries (and fails on) it.
    [Fact]
    public async Task SaveFails_ReturnsFalseAndLeavesNothingModified()
    {
        var (id, _) = await SeedAsync(verified: false);
        _factory.Auth0.EmailStatus = new Auth0EmailStatus("a@example.com", true);

        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var options = new DbContextOptionsBuilder<PrintLogContext>(services.GetRequiredService<DbContextOptions<PrintLogContext>>())
            .AddInterceptors(new FailingSaveInterceptor())
            .Options;
        using var db = new PrintLogContext(options);
        var refresher = new EmailVerificationRefresher(
            db,
            services.GetRequiredService<IAuth0Service>(),
            services.GetRequiredService<IMemoryCache>(),
            NullLogger<EmailVerificationRefresher>.Instance,
            TimeProvider.System);

        Assert.False(await refresher.RefreshAsync(id, Ct));
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State == EntityState.Modified);
    }

    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => throw new DbUpdateException("Simulated save failure.");
    }
}
