using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PrintLogApi.Achievements.Triggers;
using PrintLogApi.Email;
using PrintLogApi.IntegrationTests.Analytics;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email;

/// <summary>
/// <see cref="UserEmailSyncService"/>'s no-downgrade conditional UPDATE against a real SQL Server.
/// The SET expression reads the row's pre-update values and the WHERE mixes a nullable column with
/// a bit comparison; SQLite and SQL Server do not agree on every such translation, so the SQLite
/// suite alone cannot vouch for it.
///
/// Skipped unless PRINTLOG_SQLSERVER_TEST_CONNECTION points at a server; the database named in
/// it is ignored, and a uniquely named one is created from the migrations and dropped.
/// </summary>
public class UserEmailSyncSqlServerTests : IClassFixture<UserEmailSyncSqlServerTests.SqlServerFactory>
{
    private const string SkipReason = "Set PRINTLOG_SQLSERVER_TEST_CONNECTION to run against SQL Server.";

    private static readonly string? Connection = Environment.GetEnvironmentVariable("PRINTLOG_SQLSERVER_TEST_CONNECTION");

    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFactory _factory;

    public UserEmailSyncSqlServerTests(SqlServerFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UserEmailSyncService Sync(PrintLogContext db) => new(db, new SettableTimeProvider(Now));

    private static async Task<User> ReloadAsync(PrintLogContext db, long id)
        => await db.Users.AsNoTracking().SingleAsync(u => u.Id == id, Ct);

    [Fact]
    public async Task Sync_SameAddressUnverifiedClaim_KeepsVerified()
    {
        Assert.SkipUnless(Connection is not null, SkipReason);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db, email: "same@example.com", verified: true);

        var wrote = await Sync(db).SyncAsync(user.Id, "same@example.com", false, Ct);

        Assert.False(wrote);
        var stored = await ReloadAsync(db, user.Id);
        Assert.Equal("same@example.com", stored.Email);
        Assert.True(stored.EmailVerified);
        Assert.Null(stored.EmailUpdatedAt);
    }

    [Fact]
    public async Task Sync_ChangedAddressUnverifiedClaim_UpdatesAddressAndClearsVerified()
    {
        Assert.SkipUnless(Connection is not null, SkipReason);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db, email: "old@example.com", verified: true);

        var wrote = await Sync(db).SyncAsync(user.Id, "new@example.com", false, Ct);

        Assert.True(wrote);
        var stored = await ReloadAsync(db, user.Id);
        Assert.Equal("new@example.com", stored.Email);
        Assert.False(stored.EmailVerified);
        Assert.Equal(Now, stored.EmailUpdatedAt);
    }

    [Fact]
    public async Task Sync_SameAddressVerifiedClaim_UpgradesUnverified()
    {
        Assert.SkipUnless(Connection is not null, SkipReason);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db, email: "same@example.com", verified: false);

        var wrote = await Sync(db).SyncAsync(user.Id, "same@example.com", true, Ct);

        Assert.True(wrote);
        var stored = await ReloadAsync(db, user.Id);
        Assert.Equal("same@example.com", stored.Email);
        Assert.True(stored.EmailVerified);
        Assert.Equal(Now, stored.EmailUpdatedAt);
    }

    [Fact]
    public async Task Sync_NullStoredEmail_SetsAddress()
    {
        Assert.SkipUnless(Connection is not null, SkipReason);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db, verified: false);
        await db.Users.Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Email, (string?)null), Ct);
        Assert.Null((await ReloadAsync(db, user.Id)).Email);

        var wrote = await Sync(db).SyncAsync(user.Id, "first@example.com", false, Ct);

        Assert.True(wrote);
        var stored = await ReloadAsync(db, user.Id);
        Assert.Equal("first@example.com", stored.Email);
        Assert.False(stored.EmailVerified);
        Assert.Equal(Now, stored.EmailUpdatedAt);
    }

    /// <summary>The standard test host on a scratch SQL Server database, migrated rather than created.</summary>
    public sealed class SqlServerFactory : CustomWebApplicationFactory
    {
        private readonly string? _connection = Connection is null
            ? null
            : new SqlConnectionStringBuilder(Connection) { InitialCatalog = $"UserEmailSync_{Guid.NewGuid():N}" }.ConnectionString;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            if (_connection is null)
            {
                return;
            }

            builder.ConfigureServices(services =>
            {
                foreach (var descriptor in services.Where(d =>
                             d.ServiceType == typeof(DbContextOptions<PrintLogContext>) ||
                             d.ServiceType == typeof(DbContextOptions) ||
                             d.ServiceType == typeof(PrintLogContext) ||
                             d.ServiceType.FullName?.Contains("EntityFrameworkCore") == true).ToList())
                {
                    services.Remove(descriptor);
                }

                services.AddDbContext<PrintLogContext>((sp, options) =>
                {
                    options.UseSqlServer(_connection);
                    options.AddAchievementInterceptors(sp);
                    options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
                });
            });
        }

        protected override void PrepareDatabase(PrintLogContext db)
        {
            if (_connection is null)
            {
                base.PrepareDatabase(db);
                return;
            }

            db.Database.Migrate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _connection is not null)
            {
                var name = new SqlConnectionStringBuilder(_connection).InitialCatalog;
                var master = new SqlConnectionStringBuilder(_connection) { InitialCatalog = "master" }.ConnectionString;
                SqlConnection.ClearAllPools();
                using var conn = new SqlConnection(master);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"IF DB_ID('{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END";
                cmd.ExecuteNonQuery();
            }

            base.Dispose(disposing);
        }
    }
}
