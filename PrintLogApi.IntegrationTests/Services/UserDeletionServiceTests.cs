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

    private static UserDeletionService CreateService(PrintLogContext context, ICacheVersionService? cacheVersions = null) => new(
        context,
        NullLogger<UserDeletionService>.Instance,
        new TelemetryClient(TelemetryConfiguration.CreateDefault()),
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["PendingUserDeactivationTimeInMinutes"] = "1440" })
            .Build(),
        new TestAuth0Service(),
        new InMemoryBlobStorageService(),
        cacheVersions ?? new CacheVersionService(new MemoryCache(new MemoryCacheOptions())),
        new NoOpSpoolmanCredentialCache());

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
        public Task<PrintLogApi.Services.Auth0EmailStatus?> GetEmailStatus(
            string oauthUserId, System.Threading.CancellationToken ct) =>
            throw new NotImplementedException();

        public Task SendVerificationEmail(string oauthUserId, System.Threading.CancellationToken ct) =>
            throw new NotImplementedException();

        public Task DeleteUser(string? oauthUserId)
        {
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
