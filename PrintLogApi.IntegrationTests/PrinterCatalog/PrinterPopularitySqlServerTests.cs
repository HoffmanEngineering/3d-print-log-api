using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrintLogApi.Models;
using PrintLogApi.PrinterCatalog;
using Xunit;
using static PrintLogApi.IntegrationTests.PrinterCatalog.PrinterCatalogTestData;

namespace PrintLogApi.IntegrationTests.PrinterCatalog;

/// <summary>
/// The popularity job against real SQL Server, on a migrated scratch database, with the same
/// counts as the SQLite tests. Skipped unless PRINTLOG_SQLSERVER_TEST_CONNECTION is set.
/// </summary>
public class PrinterPopularitySqlServerTests
{
    private static readonly string? Connection = Environment.GetEnvironmentVariable("PRINTLOG_SQLSERVER_TEST_CONNECTION");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ScopeFactory(DbContextOptions<PrintLogContext> options) : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => new ScopeFactory(options);

        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType) => serviceType == typeof(PrintLogContext) ? new PrintLogContext(options) : null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task CountsOwners_LikeTheSqliteTests()
    {
        Assert.SkipUnless(Connection is not null, "Set PRINTLOG_SQLSERVER_TEST_CONNECTION to run against SQL Server.");
        var connection = new SqlConnectionStringBuilder(Connection) { InitialCatalog = $"PrinterPopularity_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<PrintLogContext>().UseSqlServer(connection).Options;
        var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

        await using (var setup = new PrintLogContext(options))
        {
            await setup.Database.MigrateAsync(Ct);
        }
        try
        {
            await using (var db = new PrintLogContext(options))
            {
                await PrinterCatalogSeeder.SeedAsync(db, File(
                    Model("recent", "Acme", "Recent"),
                    Model("printed", "Acme", "Printed"),
                    Model("inactive", "Acme", "Inactive"),
                    Model("twice", "Acme", "Twice"),
                    Model("stale", "Acme", "Stale")), now, Ct);

                var one = new User { OAuthUserId = "pop-1" };
                var two = new User { OAuthUserId = "pop-2" };
                db.Users.AddRange(one, two);
                await db.SaveChangesAsync(Ct);

                Printer P(User u, string model, DateTimeOffset? created, bool active = true) =>
                    new() { UserId = u.Id, Name = "p", IsActive = active, CatalogModelId = model, CreatedAt = created };
                var printed = P(two, "printed", now.AddMonths(-13));
                db.Printers.AddRange(
                    P(one, "recent", now.AddMonths(-1)),
                    printed,
                    P(one, "inactive", now.AddDays(-1), active: false),
                    P(one, "twice", now.AddDays(-1)),
                    P(one, "twice", now.AddMonths(-13)),
                    P(two, "stale", now.AddMonths(-13)));
                await db.SaveChangesAsync(Ct);
                db.Prints.AddRange(
                    new Print { PrinterId = printed.Id, StartDate = now.AddMonths(-1), Title = "t", CreatedById = two.Id, UpdatedById = two.Id },
                    new Print { PrinterId = db.Printers.Single(p => p.CatalogModelId == "twice" && p.CreatedAt < now.AddMonths(-12)).Id, StartDate = now.AddDays(-2), Title = "t", CreatedById = one.Id, UpdatedById = one.Id });
                await db.SaveChangesAsync(Ct);
            }

            var index = new PrinterCatalogIndex(Options.Create(new PrinterCatalogOptions()));
            var job = new PrinterPopularityJob(
                new ScopeFactory(options), index, Options.Create(new PrinterCatalogOptions()),
                TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<PrinterPopularityJob>.Instance);

            var updated = await job.RunOnceAsync(now, Ct);

            Assert.Equal(3, updated);
            await using var check = new PrintLogContext(options);
            var counts = await check.PrinterModels.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.OwnerCount, Ct);
            Assert.Equal(1, counts["recent"]);
            Assert.Equal(1, counts["printed"]);
            Assert.Equal(0, counts["inactive"]);
            Assert.Equal(1, counts["twice"]);
            Assert.Equal(0, counts["stale"]);
            Assert.Equal(1, index.Get("recent")!.OwnerCount);
        }
        finally
        {
            await using var teardown = new PrintLogContext(options);
            await teardown.Database.EnsureDeletedAsync(CancellationToken.None);
        }
    }
}
