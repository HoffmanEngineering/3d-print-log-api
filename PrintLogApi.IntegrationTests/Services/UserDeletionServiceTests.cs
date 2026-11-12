using System.Data.Common;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using PrintLogApi.Models;
using PrintLogApi.Services;
using PrintLogApi.Services.Spoolman;
using Xunit;
using static PrintLogApi.Models.User;

namespace PrintLogApi.IntegrationTests.Services;

public class UserDeletionServiceTests
{
    [Fact]
    public async Task DeleteAllDataForUser_DeletesNotificationsReferencingUserPrintsBeforePrints()
    {
        var commandInterceptor = new CommandRecordingInterceptor();
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        SqliteSearchCollation.Register(connection);

        var options = new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlite(connection)
            .AddInterceptors(commandInterceptor)
            .Options;

        await using var context = new PrintLogContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var user = new User
        {
            OAuthUserId = $"auth0|pending-delete-{Guid.NewGuid()}",
            ViewStatus = ProfileViewStatus.Public
        };
        var recipientUser = new User
        {
            OAuthUserId = $"auth0|notification-recipient-{Guid.NewGuid()}",
            ViewStatus = ProfileViewStatus.Public
        };
        context.Users.AddRange(user, recipientUser);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var printer = new Printer
        {
            Name = "Pending Delete Printer",
            UserId = user.Id,
            IsActive = true
        };
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = DateTime.UtcNow;
        var print = new Print
        {
            Title = "Pending Delete Print",
            Status = Print.PrintStatus.Success,
            ViewStatus = Print.PrintViewStatus.Public,
            PrinterId = printer.Id,
            CreatedById = user.Id,
            UpdatedById = user.Id,
            CreatedDate = now,
            UpdatedDate = now
        };
        context.Prints.Add(print);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            UserId = recipientUser.Id,
            Type = NotificationType.PrintCompleted,
            Title = "Print Completed",
            Message = "A followed print completed.",
            IsRead = false,
            CreatedDate = now,
            PrintId = print.Id
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        commandInterceptor.Clear();
        context.ChangeTracker.Clear();
        var userToDelete = await context.Users.SingleAsync(u => u.Id == user.Id, cancellationToken: TestContext.Current.CancellationToken);

        var service = new UserDeletionService(
            context,
            NullLogger<UserDeletionService>.Instance,
            new TelemetryClient(TelemetryConfiguration.CreateDefault()),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["PendingUserDeactivationTimeInMinutes"] = "1440"
                })
                .Build(),
            new TestAuth0Service(),
            new InMemoryBlobStorageService(),
            new CacheVersionService(new MemoryCache(new MemoryCacheOptions())),
            new NoOpSpoolmanCredentialCache());

        await service.DeleteAllDataForUser(userToDelete);

        var notificationDeleteIndex = commandInterceptor.Commands.FindIndex(command => IsDeleteFrom(command, "Notifications"));
        var printDeleteIndex = commandInterceptor.Commands.FindIndex(command => IsDeleteFrom(command, "Prints"));

        Assert.True(notificationDeleteIndex >= 0, "Expected notifications to be deleted during user deletion.");
        Assert.True(printDeleteIndex >= 0, "Expected prints to be deleted during user deletion.");
        Assert.True(notificationDeleteIndex < printDeleteIndex, "Notifications referencing prints must be deleted before prints.");
    }

    [Fact]
    public async Task DeleteAllDataForUser_DeletesEmailOutboxExplicitly_AndKeepsSuppressions()
    {
        var ct = TestContext.Current.CancellationToken;
        var commandInterceptor = new CommandRecordingInterceptor();
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        SqliteSearchCollation.Register(connection);

        var options = new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlite(connection)
            .AddInterceptors(commandInterceptor)
            .Options;

        await using var context = new PrintLogContext(options);
        await context.Database.EnsureCreatedAsync(ct);

        var user = new User { OAuthUserId = $"auth0|email-delete-{Guid.NewGuid()}", ViewStatus = ProfileViewStatus.Public };
        context.Users.Add(user);
        await context.SaveChangesAsync(ct);

        var now = DateTimeOffset.UtcNow;
        context.EmailOutbox.Add(new EmailOutbox
        {
            UserId = user.Id,
            Campaign = "monthly-recap",
            PeriodKey = "2026-11",
            Status = EmailOutboxStatus.Sent,
            SendAfter = now,
            NextAttemptAt = now,
            ExpiresAt = now.AddDays(5),
            CreatedAt = now,
        });
        context.EmailSuppressions.Add(new EmailSuppression { EmailHash = "hash-of-deleted-user", Reason = EmailSuppressionReason.Complaint, CreatedAt = now });
        await context.SaveChangesAsync(ct);

        commandInterceptor.Clear();
        context.ChangeTracker.Clear();
        var userToDelete = await context.Users.SingleAsync(u => u.Id == user.Id, ct);

        await CreateService(context).DeleteAllDataForUser(userToDelete);

        // The FK cascades too; the explicit delete keeps this method the one list of what a user owns.
        Assert.Contains(commandInterceptor.Commands, command => IsDeleteFrom(command, "EmailOutbox"));
        Assert.Equal(0, await context.EmailOutbox.CountAsync(ct));

        // Suppressions are keyed by address hash, not user, and must outlive the account.
        Assert.Equal(1, await context.EmailSuppressions.CountAsync(ct));
    }

    [Fact]
    public async Task DeleteAllDataForUser_DeletesWebhookSubscriptionsExplicitly()
    {
        var ct = TestContext.Current.CancellationToken;
        var commandInterceptor = new CommandRecordingInterceptor();
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        SqliteSearchCollation.Register(connection);

        var options = new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlite(connection)
            .AddInterceptors(commandInterceptor)
            .Options;

        await using var context = new PrintLogContext(options);
        await context.Database.EnsureCreatedAsync(ct);

        var user = new User { OAuthUserId = $"auth0|webhook-delete-{Guid.NewGuid()}", ViewStatus = ProfileViewStatus.Public };
        context.Users.Add(user);
        await context.SaveChangesAsync(ct);
        context.WebhookSubscriptions.Add(new WebhookSubscription
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Url = "https://hooks.example.test/api/webhook/x",
            Events = WebhookEvents.All,
            Enabled = true,
            EncryptedSecret = "v1.00000000.AAAA",
            CreatedDate = DateTime.UtcNow,
            UpdatedDate = DateTime.UtcNow,
        });
        await context.SaveChangesAsync(ct);

        commandInterceptor.Clear();
        context.ChangeTracker.Clear();
        var userToDelete = await context.Users.SingleAsync(u => u.Id == user.Id, ct);

        await CreateService(context).DeleteAllDataForUser(userToDelete);

        // The FK cascades too; the explicit delete keeps this method the one list of what a user owns.
        Assert.Contains(commandInterceptor.Commands, command => IsDeleteFrom(command, "WebhookSubscriptions"));
        Assert.Equal(0, await context.WebhookSubscriptions.CountAsync(ct));
    }

    [Fact]
    public async Task DeleteAllDataForUser_DeletesFilamentTagsBeforeFilaments()
    {
        var ct = TestContext.Current.CancellationToken;
        var commandInterceptor = new CommandRecordingInterceptor();
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        SqliteSearchCollation.Register(connection);

        var options = new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlite(connection)
            .AddInterceptors(commandInterceptor)
            .Options;

        await using var context = new PrintLogContext(options);
        await context.Database.EnsureCreatedAsync(ct);

        var user = new User { OAuthUserId = $"auth0|tag-delete-{Guid.NewGuid()}", ViewStatus = ProfileViewStatus.Public };
        context.Users.Add(user);
        await context.SaveChangesAsync(ct);

        var now = DateTime.UtcNow;
        var filament = new Filament
        {
            MaterialCategoryNickname = "filament",
            DisplayName = "Tagged",
            CreatedById = user.Id,
            UpdatedById = user.Id,
            CreatedDate = now,
            UpdatedDate = now,
        };
        context.Filaments.Add(filament);
        await context.SaveChangesAsync(ct);
        context.FilamentTags.Add(new FilamentTag { FilamentId = filament.Id, UserId = user.Id, Uid = "04A23F11", CreatedDate = now });
        await context.SaveChangesAsync(ct);

        commandInterceptor.Clear();
        context.ChangeTracker.Clear();
        var userToDelete = await context.Users.SingleAsync(u => u.Id == user.Id, ct);

        await CreateService(context).DeleteAllDataForUser(userToDelete);

        var tagDeleteIndex = commandInterceptor.Commands.FindIndex(command => IsDeleteFrom(command, "FilamentTags"));
        var filamentDeleteIndex = commandInterceptor.Commands.FindIndex(command => command.Contains("DELETE FROM \"Filaments\"", StringComparison.OrdinalIgnoreCase));
        Assert.True(tagDeleteIndex >= 0, "Expected filament tags to be deleted during user deletion.");
        Assert.True(tagDeleteIndex < filamentDeleteIndex, "Filament tags must be deleted before filaments.");
        Assert.Equal(0, await context.FilamentTags.CountAsync(ct));
    }

    [Fact]
    public async Task DeleteAllDataForUser_RemovesCatalogSkuVotes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        SqliteSearchCollation.Register(connection);

        var options = new DbContextOptionsBuilder<PrintLogContext>().UseSqlite(connection).Options;
        await using var context = new PrintLogContext(options);
        await context.Database.EnsureCreatedAsync(ct);

        var user = new User { OAuthUserId = $"auth0|sku-delete-{Guid.NewGuid()}", ViewStatus = ProfileViewStatus.Public };
        var other = new User { OAuthUserId = $"auth0|sku-keep-{Guid.NewGuid()}", ViewStatus = ProfileViewStatus.Public };
        context.Users.AddRange(user, other);
        context.FilamentCatalogEntries.Add(new FilamentCatalogEntry
        {
            Id = "test_pla_black_1000_175_n",
            Manufacturer = "Test",
            Name = "Black",
            Material = "PLA",
            Density = 1.24,
            WeightG = 1000,
            DiameterMm = 1.75,
            ImportedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync(ct);

        var now = DateTime.UtcNow;
        context.FilamentCatalogSkus.AddRange(
            new FilamentCatalogSku { Format = "snapmaker", Sku = "S1", CatalogEntryId = "test_pla_black_1000_175_n", UserId = user.Id, CreatedAt = now },
            new FilamentCatalogSku { Format = "snapmaker", Sku = "S1", CatalogEntryId = "test_pla_black_1000_175_n", UserId = other.Id, CreatedAt = now });
        await context.SaveChangesAsync(ct);

        context.ChangeTracker.Clear();
        var userToDelete = await context.Users.SingleAsync(u => u.Id == user.Id, ct);

        await CreateService(context).DeleteAllDataForUser(userToDelete);

        var remaining = await context.FilamentCatalogSkus.ToListAsync(ct);
        Assert.Equal(other.Id, Assert.Single(remaining).UserId);
        Assert.Equal(1, await context.FilamentCatalogEntries.CountAsync(ct));
    }

    /// <summary>
    /// A bridge's desired printers point at the user's printers with no database cascade (SQL
    /// Server 1785), so the bridges have to go before the printers, and after the connections
    /// that point at them.
    /// </summary>
    [Fact]
    public async Task DeleteAllDataForUser_DeletesBridgesBeforePrinters()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        SqliteSearchCollation.Register(connection);

        var options = new DbContextOptionsBuilder<PrintLogContext>().UseSqlite(connection).Options;
        await using var context = new PrintLogContext(options);
        await context.Database.EnsureCreatedAsync(ct);

        var user = new User { OAuthUserId = $"auth0|bridge-delete-{Guid.NewGuid()}", ViewStatus = ProfileViewStatus.Public };
        context.Users.Add(user);
        await context.SaveChangesAsync(ct);

        var now = DateTime.UtcNow;
        var printer = new Printer { Name = "Bridge printer", UserId = user.Id, IsActive = true };
        var grant = new DeviceGrant
        {
            UserId = user.Id,
            DeviceId = Guid.NewGuid(),
            Name = "printlog-bridge",
            TokenHash = new string('a', 64),
            Scopes = "bridge:sync",
            CreatedDate = now,
            PairedDate = now,
        };
        context.AddRange(printer, grant);
        await context.SaveChangesAsync(ct);

        context.Bridges.Add(new Bridge { Id = grant.Id, CreatedDate = now, LastSeenAt = now, DesiredRevision = Guid.NewGuid() });
        await context.SaveChangesAsync(ct);
        context.BridgePrinters.Add(new BridgePrinter { BridgeId = grant.Id, PrinterId = printer.Id, Kind = "bambu", Host = "192.168.1.60", CreatedDate = now });
        context.BridgeDiscoveredPrinters.Add(new BridgeDiscoveredPrinter
        {
            BridgeId = grant.Id,
            IdentityKey = "bambu:sn:X",
            Kind = "bambu",
            Address = "192.168.1.60",
            FirstSeenAt = now,
            LastSeenAt = now,
        });
        context.Connections.Add(new Connection
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Kind = "bambu",
            InstanceId = "i",
            DisplayName = "X1C",
            PrinterId = printer.Id,
            BridgeId = grant.Id,
            CreatedDate = now,
            LastSeenAt = now,
        });
        await context.SaveChangesAsync(ct);

        context.ChangeTracker.Clear();
        var userToDelete = await context.Users.SingleAsync(u => u.Id == user.Id, ct);

        await CreateService(context).DeleteAllDataForUser(userToDelete);

        Assert.Equal(0, await context.Bridges.CountAsync(ct));
        Assert.Equal(0, await context.BridgePrinters.CountAsync(ct));
        Assert.Equal(0, await context.BridgeDiscoveredPrinters.CountAsync(ct));
        Assert.Equal(0, await context.Printers.CountAsync(p => p.UserId == user.Id, ct));
    }

    /// <summary>
    /// Device grants go first: deleting them kills the tokens (no heartbeat or check-in can link a
    /// connection to a bridge afterwards) and cascades the bridges. Connections come after.
    /// </summary>
    [Fact]
    public async Task DeleteAllDataForUser_DeletesDeviceGrantsBeforeConnections()
    {
        var ct = TestContext.Current.CancellationToken;
        var commandInterceptor = new CommandRecordingInterceptor();
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        SqliteSearchCollation.Register(connection);

        var options = new DbContextOptionsBuilder<PrintLogContext>().UseSqlite(connection).AddInterceptors(commandInterceptor).Options;
        await using var context = new PrintLogContext(options);
        await context.Database.EnsureCreatedAsync(ct);

        var user = new User { OAuthUserId = $"auth0|grant-order-{Guid.NewGuid()}", ViewStatus = ProfileViewStatus.Public };
        context.Users.Add(user);
        await context.SaveChangesAsync(ct);

        commandInterceptor.Clear();
        context.ChangeTracker.Clear();
        await CreateService(context).DeleteAllDataForUser(await context.Users.SingleAsync(u => u.Id == user.Id, ct));

        var grants = commandInterceptor.Commands.FindIndex(c => c.Contains("DELETE FROM \"DeviceGrants\"", StringComparison.OrdinalIgnoreCase));
        var connections = commandInterceptor.Commands.FindIndex(c => c.Contains("DELETE FROM \"Connections\"", StringComparison.OrdinalIgnoreCase));
        Assert.True(grants >= 0 && connections >= 0, "expected both deletes");
        Assert.True(grants < connections, "device grants must be deleted before connections");
    }

    [Fact]
    public async Task DeleteAllDataForUser_InvalidatesTheUsersCacheVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        SqliteSearchCollation.Register(connection);

        var options = new DbContextOptionsBuilder<PrintLogContext>().UseSqlite(connection).Options;
        await using var context = new PrintLogContext(options);
        await context.Database.EnsureCreatedAsync(ct);

        var user = new User { OAuthUserId = $"auth0|version-delete-{Guid.NewGuid()}", ViewStatus = ProfileViewStatus.Public };
        context.Users.Add(user);
        await context.SaveChangesAsync(ct);

        // One instance for the service and the assertion, so the bump is observable (#194).
        var cacheVersions = new CacheVersionService(new MemoryCache(new MemoryCacheOptions()));
        var before = cacheVersions.GetUserCacheVersion(user.Id);

        await CreateService(context, cacheVersions).DeleteAllDataForUser(user);

        Assert.NotEqual(before, cacheVersions.GetUserCacheVersion(user.Id));
    }

    private static UserDeletionService CreateService(
        PrintLogContext context,
        ICacheVersionService? cacheVersions = null,
        TestAuth0Service? auth0 = null,
        TimeProvider? clock = null) => new(
        context,
        NullLogger<UserDeletionService>.Instance,
        new TelemetryClient(TelemetryConfiguration.CreateDefault()),
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["PendingUserDeactivationTimeInMinutes"] = "1440" })
            .Build(),
        auth0 ?? new TestAuth0Service(),
        new InMemoryBlobStorageService(),
        cacheVersions ?? new CacheVersionService(new MemoryCache(new MemoryCacheOptions())),
        new NoOpSpoolmanCredentialCache(),
        clock);

    #region DeletePendingDeactivatedUsers

    // Far from the real date on purpose: a cutoff computed from the wall clock instead of the
    // injected one would put every user below in the future, and none would be deleted.
    private static readonly DateTimeOffset PinnedNow = new(2030, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static async Task<(SqliteConnection Connection, PrintLogContext Context)> CreateDatabaseAsync(params IInterceptor[] interceptors)
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        SqliteSearchCollation.Register(connection);

        var builder = new DbContextOptionsBuilder<PrintLogContext>().UseSqlite(connection);
        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }

        var context = new PrintLogContext(builder.Options);
        await context.Database.EnsureCreatedAsync(ct);
        return (connection, context);
    }

    private static async Task<User> AddUserAsync(PrintLogContext context, string name, DateTimeOffset? deactivatedAt, bool withPrinter = false)
    {
        var ct = TestContext.Current.CancellationToken;
        var user = new User
        {
            OAuthUserId = $"auth0|{name}-{Guid.NewGuid()}",
            DeactivationDateTime = deactivatedAt,
            ViewStatus = ProfileViewStatus.Public,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync(ct);

        if (withPrinter)
        {
            context.Printers.Add(new Printer { Name = $"{name} printer", UserId = user.Id, IsActive = true });
            await context.SaveChangesAsync(ct);
        }

        // The service loads its users fresh, as it does from a new scope in production. Left
        // tracked, the user would carry its printer navigation into Remove, and SaveChanges would
        // try to delete a printer row ExecuteDelete has already removed.
        context.ChangeTracker.Clear();
        return user;
    }

    private static async Task<long[]> RemainingUserIdsAsync(PrintLogContext context)
    {
        context.ChangeTracker.Clear();
        return await context.Users.OrderBy(u => u.Id).Select(u => u.Id).ToArrayAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DeletePendingDeactivatedUsers_DeletesOnlyUsersPastTheGracePeriod()
    {
        var (connection, context) = await CreateDatabaseAsync();
        await using var _ = connection;
        await using var __ = context;

        var expired = await AddUserAsync(context, "expired", PinnedNow.AddDays(-2));
        var justExpired = await AddUserAsync(context, "just-expired", PinnedNow.AddMinutes(-1440));
        var insideGrace = await AddUserAsync(context, "inside-grace", PinnedNow.AddMinutes(-1439));
        var active = await AddUserAsync(context, "active", deactivatedAt: null);
        var auth0 = new TestAuth0Service();

        var result = await CreateService(context, auth0: auth0, clock: new Analytics.SettableTimeProvider(PinnedNow))
            .DeletePendingDeactivatedUsers(TestContext.Current.CancellationToken);

        Assert.Equal([insideGrace.Id, active.Id], await RemainingUserIdsAsync(context));
        Assert.Equal(new PendingUserDeletionResult(Candidates: 2, Deleted: 2, Failed: 0), result);
        Assert.Equal([expired.OAuthUserId, justExpired.OAuthUserId], auth0.DeleteCalls.Order());
    }

    /// <summary>
    /// One account's Auth0 outage used to abort the whole batch, and since every run read the
    /// same users first, it aborted every later run too. Now that user is skipped, the rest are
    /// deleted, and it is tried again next run. Auth0 goes first, so a user whose Auth0 delete
    /// failed still has every row: nothing about them is half deleted.
    /// </summary>
    [Fact]
    public async Task DeletePendingDeactivatedUsers_WhenAuth0FailsForOneUser_DeletesTheOthers_AndRetriesItNextRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var (connection, context) = await CreateDatabaseAsync();
        await using var _ = connection;
        await using var __ = context;

        var failing = await AddUserAsync(context, "auth0-fails", PinnedNow.AddDays(-3), withPrinter: true);
        var healthy = await AddUserAsync(context, "healthy", PinnedNow.AddDays(-2), withPrinter: true);
        var auth0 = new TestAuth0Service();
        auth0.FailDeleteFor.Add(failing.OAuthUserId!);
        var clock = new Analytics.SettableTimeProvider(PinnedNow);

        var first = await CreateService(context, auth0: auth0, clock: clock).DeletePendingDeactivatedUsers(ct);

        Assert.Equal(new PendingUserDeletionResult(Candidates: 2, Deleted: 1, Failed: 1), first);
        Assert.Equal([failing.Id], await RemainingUserIdsAsync(context));
        Assert.Equal(1, await context.Printers.CountAsync(p => p.UserId == failing.Id, ct));
        Assert.Equal(0, await context.Printers.CountAsync(p => p.UserId == healthy.Id, ct));

        auth0.FailDeleteFor.Clear();
        var second = await CreateService(context, auth0: auth0, clock: clock).DeletePendingDeactivatedUsers(ct);

        Assert.Equal(new PendingUserDeletionResult(Candidates: 1, Deleted: 1, Failed: 0), second);
        Assert.Empty(await RemainingUserIdsAsync(context));
        Assert.Equal(0, await context.Printers.CountAsync(ct));
        Assert.Equal(2, auth0.DeleteCalls.Count(id => id == failing.OAuthUserId));
    }

    /// <summary>
    /// The other partial failure: Auth0 has already deleted the account when a database statement
    /// fails. The user row is still there, so the next run picks the user up again, repeats the
    /// Auth0 delete (a no-op for an account that is gone) and finishes the rows. The failed user's
    /// tracked delete must not leak into the next user's SaveChanges, or one failure fails both.
    /// </summary>
    [Fact]
    public async Task DeletePendingDeactivatedUsers_WhenTheDatabaseDeleteFails_DeletesTheOthers_AndFinishesItNextRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var failUserDelete = new FailUserRowDeleteInterceptor();
        var (connection, context) = await CreateDatabaseAsync(failUserDelete);
        await using var _ = connection;
        await using var __ = context;

        var failing = await AddUserAsync(context, "db-fails", PinnedNow.AddDays(-3), withPrinter: true);
        var healthy = await AddUserAsync(context, "healthy", PinnedNow.AddDays(-2), withPrinter: true);
        failUserDelete.UserId = failing.Id;
        var auth0 = new TestAuth0Service();
        var clock = new Analytics.SettableTimeProvider(PinnedNow);

        var first = await CreateService(context, auth0: auth0, clock: clock).DeletePendingDeactivatedUsers(ct);

        Assert.Equal(new PendingUserDeletionResult(Candidates: 2, Deleted: 1, Failed: 1), first);
        Assert.Equal([failing.Id], await RemainingUserIdsAsync(context));
        Assert.Equal(0, await context.Printers.CountAsync(p => p.UserId == healthy.Id, ct));

        failUserDelete.UserId = null;
        var second = await CreateService(context, auth0: auth0, clock: clock).DeletePendingDeactivatedUsers(ct);

        Assert.Equal(new PendingUserDeletionResult(Candidates: 1, Deleted: 1, Failed: 0), second);
        Assert.Empty(await RemainingUserIdsAsync(context));
        Assert.Equal(2, auth0.DeleteCalls.Count(id => id == failing.OAuthUserId));
    }

    [Fact]
    public async Task DeletePendingDeactivatedUsers_UserWithoutAnAuth0Id_IsDeletedWithoutCallingAuth0()
    {
        var (connection, context) = await CreateDatabaseAsync();
        await using var _ = connection;
        await using var __ = context;

        var user = new User { OAuthUserId = null, DeactivationDateTime = PinnedNow.AddDays(-2), ViewStatus = ProfileViewStatus.Public };
        context.Users.Add(user);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var auth0 = new TestAuth0Service();

        var result = await CreateService(context, auth0: auth0, clock: new Analytics.SettableTimeProvider(PinnedNow))
            .DeletePendingDeactivatedUsers(TestContext.Current.CancellationToken);

        Assert.Equal(new PendingUserDeletionResult(Candidates: 1, Deleted: 1, Failed: 0), result);
        Assert.Empty(await RemainingUserIdsAsync(context));
        Assert.Empty(auth0.DeleteCalls);
    }

    /// <summary>
    /// Another user's usage row on the deleted user's spool (a legacy row, from before spool
    /// ownership was checked) used to block the Filaments delete (a Restrict FK) on every run. It
    /// is detached instead, as deleting a spool detaches it (FilamentService.DeleteFilament): the
    /// row keeps its amount, with no spool, and its owner's cached summaries are invalidated.
    /// </summary>
    [Fact]
    public async Task DeletePendingDeactivatedUsers_DetachesOtherUsersUsageRowsFromTheUsersSpools()
    {
        var ct = TestContext.Current.CancellationToken;
        var (connection, context) = await CreateDatabaseAsync();
        await using var _ = connection;
        await using var __ = context;

        var deleted = await AddUserAsync(context, "spool-owner", PinnedNow.AddDays(-2));
        var other = await AddUserAsync(context, "print-owner", deactivatedAt: null, withPrinter: true);
        var now = DateTime.UtcNow;
        var spool = new Filament
        {
            MaterialCategoryNickname = "filament",
            DisplayName = "Borrowed spool",
            CreatedById = deleted.Id,
            UpdatedById = deleted.Id,
            CreatedDate = now,
            UpdatedDate = now,
        };
        context.Filaments.Add(spool);
        var print = new Print
        {
            Title = "Other user's print",
            Status = Print.PrintStatus.Success,
            ViewStatus = Print.PrintViewStatus.Public,
            PrinterId = await context.Printers.Where(p => p.UserId == other.Id).Select(p => p.Id).SingleAsync(ct),
            CreatedById = other.Id,
            UpdatedById = other.Id,
            CreatedDate = now,
            UpdatedDate = now,
        };
        context.Prints.Add(print);
        await context.SaveChangesAsync(ct);
        var usage = new PrintFilament { Id = Guid.NewGuid(), PrintId = print.Id, FilamentId = spool.Id, AmountMg = 12_345 };
        context.PrintFilament.Add(usage);
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();
        var cacheVersions = new CacheVersionService(new MemoryCache(new MemoryCacheOptions()));
        var otherVersionBefore = cacheVersions.GetUserCacheVersion(other.Id);

        var result = await CreateService(context, cacheVersions, clock: new Analytics.SettableTimeProvider(PinnedNow))
            .DeletePendingDeactivatedUsers(ct);

        Assert.Equal(new PendingUserDeletionResult(Candidates: 1, Deleted: 1, Failed: 0), result);
        Assert.Equal([other.Id], await RemainingUserIdsAsync(context));
        var row = await context.PrintFilament.AsNoTracking().SingleAsync(pf => pf.Id == usage.Id, ct);
        Assert.Null(row.FilamentId);
        Assert.Equal(12_345, row.AmountMg);
        Assert.Equal(print.Id, row.PrintId);
        Assert.False(await context.Filaments.IgnoreQueryFilters().AnyAsync(f => f.Id == spool.Id, ct));
        Assert.NotEqual(otherVersionBefore, cacheVersions.GetUserCacheVersion(other.Id));
    }

    /// <summary>
    /// Other users' comments on the deleted user's prints went with nothing pointing at them, and
    /// the user's MCP idempotency records (no FK) stayed forever. Both are deleted now, along with
    /// a notification about such a comment (its FK to the comment does not cascade). Other users'
    /// comments elsewhere and their own idempotency records stay.
    /// </summary>
    [Fact]
    public async Task DeletePendingDeactivatedUsers_DeletesOtherUsersCommentsOnTheUsersPrints_AndTheUsersMcpRecords()
    {
        var ct = TestContext.Current.CancellationToken;
        var (connection, context) = await CreateDatabaseAsync();
        await using var _ = connection;
        await using var __ = context;

        var deleted = await AddUserAsync(context, "print-owner", PinnedNow.AddDays(-2), withPrinter: true);
        var commenter = await AddUserAsync(context, "commenter", deactivatedAt: null, withPrinter: true);
        var now = DateTime.UtcNow;
        Print NewPrint(long ownerId, long printerId) => new()
        {
            Title = $"print of {ownerId}",
            Status = Print.PrintStatus.Success,
            ViewStatus = Print.PrintViewStatus.Public,
            PrinterId = printerId,
            CreatedById = ownerId,
            UpdatedById = ownerId,
            CreatedDate = now,
            UpdatedDate = now,
        };
        var deletedUsersPrint = NewPrint(deleted.Id, await context.Printers.Where(p => p.UserId == deleted.Id).Select(p => p.Id).SingleAsync(ct));
        var commentersPrint = NewPrint(commenter.Id, await context.Printers.Where(p => p.UserId == commenter.Id).Select(p => p.Id).SingleAsync(ct));
        context.Prints.AddRange(deletedUsersPrint, commentersPrint);
        var onDeletedUsersPrint = new Comment { Body = "nice", CreatedById = commenter.Id, UpdatedById = commenter.Id, CreatedDate = now, UpdatedDate = now };
        var onOwnPrint = new Comment { Body = "mine", CreatedById = commenter.Id, UpdatedById = commenter.Id, CreatedDate = now, UpdatedDate = now };
        context.Comments.AddRange(onDeletedUsersPrint, onOwnPrint);
        await context.SaveChangesAsync(ct);
        context.PrintComments.AddRange(
            new PrintComment { PrintId = deletedUsersPrint.Id, CommentId = onDeletedUsersPrint.Id, CreatedById = commenter.Id, UpdatedById = commenter.Id, CreatedDate = now, UpdatedDate = now },
            new PrintComment { PrintId = commentersPrint.Id, CommentId = onOwnPrint.Id, CreatedById = commenter.Id, UpdatedById = commenter.Id, CreatedDate = now, UpdatedDate = now });
        context.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            UserId = commenter.Id,
            Type = NotificationType.PrintCompleted,
            Title = "About the comment",
            Message = "m",
            CreatedDate = now,
            CommentId = onDeletedUsersPrint.Id,
        });
        context.McpIdempotencyRecords.AddRange(
            new McpIdempotencyRecord { UserId = deleted.Id, ToolName = "create_print", IdempotencyKey = "k-deleted", CreatedPrintId = deletedUsersPrint.Id, CreatedAt = PinnedNow },
            new McpIdempotencyRecord { UserId = commenter.Id, ToolName = "create_print", IdempotencyKey = "k-kept", CreatedPrintId = commentersPrint.Id, CreatedAt = PinnedNow });
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();

        var result = await CreateService(context, clock: new Analytics.SettableTimeProvider(PinnedNow)).DeletePendingDeactivatedUsers(ct);

        Assert.Equal(new PendingUserDeletionResult(Candidates: 1, Deleted: 1, Failed: 0), result);
        Assert.Equal([commenter.Id], await RemainingUserIdsAsync(context));
        Assert.Equal([onOwnPrint.Id], await context.Comments.Select(c => c.Id).ToListAsync(ct));
        Assert.Equal(0, await context.Notifications.CountAsync(ct));
        Assert.Equal(["k-kept"], await context.McpIdempotencyRecords.Select(r => r.IdempotencyKey).ToListAsync(ct));
    }

    /// <summary>Fails the final <c>DELETE FROM "Users"</c> for one user id, as a database error there would.</summary>
    private sealed class FailUserRowDeleteInterceptor : DbCommandInterceptor
    {
        public long? UserId { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfTargeted(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfTargeted(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void ThrowIfTargeted(DbCommand command)
        {
            if (UserId is not { } id
                || !command.CommandText.Contains("DELETE FROM \"Users\"", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (DbParameter parameter in command.Parameters)
            {
                if (parameter.Value is long value && value == id)
                {
                    throw new InvalidOperationException($"Simulated database failure deleting user {id}.");
                }
            }
        }
    }

    #endregion

    /// <summary>These tests assert on SQL, not on eviction; SpoolmanApiTests covers that end to end.</summary>
    private sealed class NoOpSpoolmanCredentialCache : ISpoolmanCredentialCache
    {
        public Task<SpoolmanCredential> ResolveAsync(string secretHash, CancellationToken ct) => Task.FromResult(SpoolmanCredential.Unknown);

        public Task InvalidateAsync(IEnumerable<string> secretHashes) => Task.CompletedTask;

        public Task InvalidateConnectionsAsync(PrintLogContext context, IEnumerable<Guid> connectionIds) => Task.CompletedTask;
    }

    private static bool IsDeleteFrom(string command, string tableName)
    {
        return command.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase)
            && command.Contains(tableName, StringComparison.OrdinalIgnoreCase);
    }

    private class CommandRecordingInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = new List<string>();

        public void Clear()
        {
            Commands.Clear();
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Commands.Add(command.CommandText);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private class TestAuth0Service : IAuth0Service
    {
        /// <summary>Every DeleteUser call, including the ones that threw.</summary>
        public List<string?> DeleteCalls { get; } = new();

        /// <summary>Oauth ids whose DeleteUser throws, as an Auth0 outage for that account would.</summary>
        public HashSet<string> FailDeleteFor { get; } = new();

        public Task<PrintLogApi.Services.Auth0EmailStatus?> GetEmailStatus(
            string oauthUserId, System.Threading.CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<string?> GetUserPicture(string oauthUserId, System.Threading.CancellationToken ct) =>
            throw new NotImplementedException();

        public Task SendVerificationEmail(string oauthUserId, System.Threading.CancellationToken ct) =>
            throw new NotImplementedException();

        public Task DeleteUser(string? oauthUserId)
        {
            DeleteCalls.Add(oauthUserId);
            if (oauthUserId != null && FailDeleteFor.Contains(oauthUserId))
            {
                throw new PrintLogApi.Exceptions.Auth0ApiException("Auth0 Management API request failed with status 503.");
            }

            return Task.CompletedTask;
        }

        public Task<string> GetManagementApiBearerToken()
        {
            return Task.FromResult("test-token");
        }

        public Task<string?> GetUserEmail(string oauthUserId, System.Threading.CancellationToken ct)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<System.Collections.Generic.IReadOnlyList<PrintLogApi.Models.DTOs.ConnectedAgentDto>> ListMcpGrants(
            string authUserId, System.Threading.CancellationToken ct)
        {
            return Task.FromResult<System.Collections.Generic.IReadOnlyList<PrintLogApi.Models.DTOs.ConnectedAgentDto>>(
                new System.Collections.Generic.List<PrintLogApi.Models.DTOs.ConnectedAgentDto>());
        }

        public Task RevokeMcpGrant(string authUserId, string grantId, System.Threading.CancellationToken ct)
        {
            return Task.CompletedTask;
        }
    }
}
