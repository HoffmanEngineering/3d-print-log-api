using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Email;
using PrintLogApi.IntegrationTests.Analytics;
using PrintLogApi.Models;
using PrintLogApi.Users;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email;

public class UserEmailSyncTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly CustomWebApplicationFactory _factory;

    public UserEmailSyncTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static UserEmailSyncService Sync(PrintLogContext db) => new(db, new SettableTimeProvider(Now));

    private static async Task<User> ReloadAsync(PrintLogContext db, long id)
        => await db.Users.AsNoTracking().SingleAsync(u => u.Id == id, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Sync_WritesWhenChanged()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db, email: "old@example.com", verified: false);

        var wrote = await Sync(db).SyncAsync(user.Id, "new@example.com", true, TestContext.Current.CancellationToken);

        Assert.True(wrote);
        var stored = await ReloadAsync(db, user.Id);
        Assert.Equal("new@example.com", stored.Email);
        Assert.True(stored.EmailVerified);
        Assert.Equal(Now, stored.EmailUpdatedAt);
    }

    [Fact]
    public async Task Sync_VerifiedFlagChangeAloneIsAWrite()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db, email: "same@example.com", verified: false);

        Assert.True(await Sync(db).SyncAsync(user.Id, "same@example.com", true, TestContext.Current.CancellationToken));
        Assert.True((await ReloadAsync(db, user.Id)).EmailVerified);
    }

    [Fact]
    public async Task Sync_NoWriteWhenUnchanged()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db, email: "same@example.com", verified: true);

        var wrote = await Sync(db).SyncAsync(user.Id, "same@example.com", true, TestContext.Current.CancellationToken);

        Assert.False(wrote);
        Assert.Null((await ReloadAsync(db, user.Id)).EmailUpdatedAt);
    }

    // A token minted before the Auth0 Action was deployed carries no email claim. That is
    // "unknown", not "the user removed their address", so it must never blank a stored one.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Sync_IgnoresMissingEmailClaim(string? email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db, email: "kept@example.com");

        Assert.False(await Sync(db).SyncAsync(user.Id, email, true, TestContext.Current.CancellationToken));
        Assert.Equal("kept@example.com", (await ReloadAsync(db, user.Id)).Email);
    }

    [Fact]
    public async Task Sync_TrimsTheAddress()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db, email: "a@example.com");

        await Sync(db).SyncAsync(user.Id, "  b@example.com ", true, TestContext.Current.CancellationToken);

        Assert.Equal("b@example.com", (await ReloadAsync(db, user.Id)).Email);
    }

    [Fact]
    public async Task ClaimsTransformer_SyncsEmailClaimOncePerDay()
    {
        var ct = TestContext.Current.CancellationToken;
        long userId;
        string oauthId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var user = await EmailTestData.CreateUserAsync(db, email: "before@example.com", verified: false);
            userId = user.Id;
            oauthId = user.OAuthUserId!;
        }

        var client = _factory.CreateClient();

        async Task GetMeWithEmail(string email)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/Users/me");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, oauthId);
            request.Headers.Add(TestAuthHandler.TestEmailHeader, email);
            (await client.SendAsync(request, ct)).EnsureSuccessStatusCode();
        }

        async Task<string?> StoredEmail()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            return (await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct)).Email;
        }

        await GetMeWithEmail("claim@example.com");
        Assert.Equal("claim@example.com", await StoredEmail());

        // Overwrite behind the cache's back: the same claim again is a cache hit and must not
        // touch the database, which is the whole point of caching the sync per claim value.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            await db.Users.Where(u => u.Id == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.Email, "manual@example.com"), ct);
        }

        await GetMeWithEmail("claim@example.com");
        Assert.Equal("manual@example.com", await StoredEmail());

        // A changed claim is a new cache key, so it syncs immediately rather than a day later.
        await GetMeWithEmail("changed@example.com");
        Assert.Equal("changed@example.com", await StoredEmail());
    }

    [Fact]
    public async Task CreateUserFromAuthId_SetsCreatedDate()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var telemetry = new TelemetryClient(new TelemetryConfiguration());
        var users = new UserService(db, telemetry, new SettableTimeProvider(Now));

        var created = await users.CreateUserFromAuthId($"auth0|created-{Guid.NewGuid():N}");

        Assert.Equal(Now, (await ReloadAsync(db, created.Id)).CreatedDate);
    }
}
