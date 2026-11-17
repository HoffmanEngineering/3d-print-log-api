using System.Net;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using PrintLogApi.IntegrationTests.Billing;
using PrintLogApi.Models;
using PrintLogApi.Services;
using PrintLogApi.Services.Spoolman;
using Xunit;
using static PrintLogApi.Models.User;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// Account deletion and the user's Stripe subscription. Any subscription Stripe may still bill
/// (one with a Stripe id that is not canceled locally) is canceled, whatever its local status, and
/// a cancel that fails stops that user's deletion before any row is gone, so the next run retries.
/// </summary>
[Collection(StripeClientCollection.Name)]
public class UserDeletionStripeTests
{
    private static readonly DateTimeOffset PinnedNow = new(2030, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.PastDue)]
    [InlineData(SubscriptionStatus.None)]
    public async Task Deletion_CancelsAnySubscriptionNotCanceled(SubscriptionStatus status)
    {
        var (connection, context) = await CreateDatabaseAsync();
        await using var _ = connection;
        await using var __ = context;
        var stripe = new FakeStripe { SubscriptionJson = id => FakeStripe.SubscriptionObjectJson(id, "canceled") };
        using var install = stripe.Install();
        var user = await AddDeactivatedUserAsync(context, "billing");
        await AddSubscriptionAsync(context, user.Id, status, "sub_to_cancel");

        var result = await CreateService(context).DeletePendingDeactivatedUsers(Ct);

        Assert.Equal(new PendingUserDeletionResult(1, 1, 0), result);
        Assert.Contains(stripe.Requests, r => r.Method == HttpMethod.Delete && r.Path == "/v1/subscriptions/sub_to_cancel");
        context.ChangeTracker.Clear();
        Assert.Equal(0, await context.Users.CountAsync(Ct));
        Assert.Equal(0, await context.Subscriptions.CountAsync(Ct));
    }

    [Fact]
    public async Task Deletion_DoesNotCallStripe_ForACanceledSubscription_OrOneWithoutAStripeId()
    {
        var (connection, context) = await CreateDatabaseAsync();
        await using var _ = connection;
        await using var __ = context;
        var stripe = new FakeStripe();
        using var install = stripe.Install();
        var canceled = await AddDeactivatedUserAsync(context, "canceled");
        await AddSubscriptionAsync(context, canceled.Id, SubscriptionStatus.Canceled, "sub_already_canceled");
        var free = await AddDeactivatedUserAsync(context, "no-stripe-id");
        await AddSubscriptionAsync(context, free.Id, SubscriptionStatus.None, stripeSubscriptionId: null);

        var result = await CreateService(context).DeletePendingDeactivatedUsers(Ct);

        Assert.Equal(new PendingUserDeletionResult(2, 2, 0), result);
        Assert.Empty(stripe.Requests);
    }

    /// <summary>
    /// A subscription Stripe no longer knows (404) is as good as canceled: nothing can bill it.
    /// </summary>
    [Fact]
    public async Task Deletion_TreatsNoSuchSubscriptionAsCanceled()
    {
        var (connection, context) = await CreateDatabaseAsync();
        await using var _ = connection;
        await using var __ = context;
        var stripe = new FakeStripe
        {
            Respond = (_, _) => FakeStripe.Error(HttpStatusCode.NotFound, "resource_missing", "No such subscription: 'sub_gone'"),
        };
        using var install = stripe.Install();
        var user = await AddDeactivatedUserAsync(context, "gone");
        await AddSubscriptionAsync(context, user.Id, SubscriptionStatus.PastDue, "sub_gone");

        var result = await CreateService(context).DeletePendingDeactivatedUsers(Ct);

        Assert.Equal(new PendingUserDeletionResult(1, 1, 0), result);
        context.ChangeTracker.Clear();
        Assert.Equal(0, await context.Users.CountAsync(Ct));
    }

    /// <summary>
    /// A cancel Stripe refuses because the subscription is already canceled (canceled in Stripe
    /// before the webhook reached us) is done, too: a read confirms it.
    /// </summary>
    [Fact]
    public async Task Deletion_TreatsARefusedCancelOfAnAlreadyCanceledSubscriptionAsDone()
    {
        var (connection, context) = await CreateDatabaseAsync();
        await using var _ = connection;
        await using var __ = context;
        var stripe = new FakeStripe
        {
            SubscriptionJson = id => FakeStripe.SubscriptionObjectJson(id, "canceled"),
            Respond = (method, _) => method == HttpMethod.Delete
                ? FakeStripe.Error(HttpStatusCode.BadRequest, "subscription_canceled", "This subscription is already canceled.")
                : null,
        };
        using var install = stripe.Install();
        var user = await AddDeactivatedUserAsync(context, "stale-status");
        await AddSubscriptionAsync(context, user.Id, SubscriptionStatus.Active, "sub_stale");

        var result = await CreateService(context).DeletePendingDeactivatedUsers(Ct);

        Assert.Equal(new PendingUserDeletionResult(1, 1, 0), result);
        Assert.Contains(stripe.Requests, r => r.Method == HttpMethod.Get && r.Path == "/v1/subscriptions/sub_stale");
    }

    /// <summary>
    /// A cancel that fails for any other reason fails that user before anything local is deleted:
    /// the subscription row is the only record of what Stripe may keep billing. The other users in
    /// the run are still deleted, and the next run retries this one.
    /// </summary>
    [Fact]
    public async Task Deletion_WhenTheCancelFails_KeepsTheUsersRows_DeletesTheOthers_AndRetriesNextRun()
    {
        var (connection, context) = await CreateDatabaseAsync();
        await using var _ = connection;
        await using var __ = context;
        var stripeDown = true;
        var stripe = new FakeStripe
        {
            SubscriptionJson = id => FakeStripe.SubscriptionObjectJson(id, stripeDown ? "past_due" : "canceled"),
            Respond = (method, _) => stripeDown && method == HttpMethod.Delete
                ? FakeStripe.Error(HttpStatusCode.InternalServerError, "api_error", "Something went wrong.")
                : null,
        };
        using var install = stripe.Install();
        var failing = await AddDeactivatedUserAsync(context, "stripe-fails", withPrinter: true);
        await AddSubscriptionAsync(context, failing.Id, SubscriptionStatus.PastDue, "sub_retry");
        var healthy = await AddDeactivatedUserAsync(context, "healthy", withPrinter: true);
        var auth0 = new RecordingAuth0Service();

        var first = await CreateService(context, auth0).DeletePendingDeactivatedUsers(Ct);

        Assert.Equal(new PendingUserDeletionResult(2, 1, 1), first);
        context.ChangeTracker.Clear();
        Assert.Equal([failing.Id], await context.Users.Select(u => u.Id).ToArrayAsync(Ct));
        Assert.Equal("sub_retry", (await context.Subscriptions.AsNoTracking().SingleAsync(Ct)).StripeSubscriptionId);
        Assert.Equal(1, await context.Printers.CountAsync(p => p.UserId == failing.Id, Ct));
        Assert.Equal(0, await context.Printers.CountAsync(p => p.UserId == healthy.Id, Ct));

        // Stripe.net retries a 5xx itself, so the first run may have sent the cancel more than once.
        var cancelsBefore = stripe.Requests.Count(r => r.Method == HttpMethod.Delete && r.Path == "/v1/subscriptions/sub_retry");
        stripeDown = false;
        var second = await CreateService(context, auth0).DeletePendingDeactivatedUsers(Ct);

        Assert.Equal(new PendingUserDeletionResult(1, 1, 0), second);
        context.ChangeTracker.Clear();
        Assert.Equal(0, await context.Users.CountAsync(Ct));
        Assert.Equal(0, await context.Subscriptions.CountAsync(Ct));
        Assert.Equal(cancelsBefore + 1, stripe.Requests.Count(r => r.Method == HttpMethod.Delete && r.Path == "/v1/subscriptions/sub_retry"));
        Assert.Equal(2, auth0.DeleteCalls.Count(id => id == failing.OAuthUserId));
    }

    private static async Task<(SqliteConnection Connection, PrintLogContext Context)> CreateDatabaseAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(Ct);
        SqliteSearchCollation.Register(connection);
        var context = new PrintLogContext(new DbContextOptionsBuilder<PrintLogContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync(Ct);
        return (connection, context);
    }

    private static async Task<User> AddDeactivatedUserAsync(PrintLogContext context, string name, bool withPrinter = false)
    {
        var user = new User
        {
            OAuthUserId = $"auth0|{name}-{Guid.NewGuid():N}",
            DeactivationDateTime = PinnedNow.AddDays(-2),
            ViewStatus = ProfileViewStatus.Public,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync(Ct);
        if (withPrinter)
        {
            context.Printers.Add(new Printer { Name = $"{name} printer", UserId = user.Id, IsActive = true });
            await context.SaveChangesAsync(Ct);
        }

        context.ChangeTracker.Clear();
        return user;
    }

    private static async Task AddSubscriptionAsync(PrintLogContext context, long userId, SubscriptionStatus status, string? stripeSubscriptionId)
    {
        context.Subscriptions.Add(new Subscription
        {
            UserId = userId,
            Status = status,
            Plan = SubscriptionPlan.ProMonthly,
            StripeCustomerId = $"cus_{userId}",
            StripeSubscriptionId = stripeSubscriptionId,
            CreatedById = userId,
            UpdatedById = userId,
        });
        await context.SaveChangesAsync(Ct);
        context.ChangeTracker.Clear();
    }

    private static UserDeletionService CreateService(PrintLogContext context, IAuth0Service? auth0 = null) => new(
        context,
        NullLogger<UserDeletionService>.Instance,
        new TelemetryClient(TelemetryConfiguration.CreateDefault()),
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["PendingUserDeactivationTimeInMinutes"] = "1440" })
            .Build(),
        auth0 ?? new RecordingAuth0Service(),
        new InMemoryBlobStorageService(),
        new CacheVersionService(new MemoryCache(new MemoryCacheOptions())),
        new NoOpSpoolmanCredentialCache(),
        new Analytics.SettableTimeProvider(PinnedNow));

    private sealed class NoOpSpoolmanCredentialCache : ISpoolmanCredentialCache
    {
        public Task<SpoolmanCredential> ResolveAsync(string secretHash, CancellationToken ct) => Task.FromResult(SpoolmanCredential.Unknown);

        public Task InvalidateAsync(IEnumerable<string> secretHashes) => Task.CompletedTask;

        public Task InvalidateConnectionsAsync(PrintLogContext context, IEnumerable<Guid> connectionIds) => Task.CompletedTask;
    }

    private sealed class RecordingAuth0Service : IAuth0Service
    {
        public List<string?> DeleteCalls { get; } = new();

        public Task DeleteUser(string? oauthUserId)
        {
            DeleteCalls.Add(oauthUserId);
            return Task.CompletedTask;
        }

        public Task<Auth0EmailStatus?> GetEmailStatus(string oauthUserId, CancellationToken ct) => throw new NotImplementedException();

        public Task<string?> GetUserPicture(string oauthUserId, CancellationToken ct) => throw new NotImplementedException();

        public Task SendVerificationEmail(string oauthUserId, CancellationToken ct) => throw new NotImplementedException();

        public Task<string> GetManagementApiBearerToken() => Task.FromResult("test-token");

        public Task<string?> GetUserEmail(string oauthUserId, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<PrintLogApi.Models.DTOs.ConnectedAgentDto>> ListMcpGrants(string authUserId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PrintLogApi.Models.DTOs.ConnectedAgentDto>>([]);

        public Task RevokeMcpGrant(string authUserId, string grantId, CancellationToken ct) => Task.CompletedTask;
    }
}
