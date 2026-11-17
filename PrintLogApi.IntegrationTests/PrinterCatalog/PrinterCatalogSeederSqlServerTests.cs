using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.PrinterCatalog;
using PrintLogApi.PrinterCatalog.Generation;
using Xunit;

namespace PrintLogApi.IntegrationTests.PrinterCatalog;

/// <summary>
/// The seeder against real SQL Server, on a migrated scratch database: the transaction inside a
/// retrying execution strategy, the bulk upsert of the embedded catalog, and the re-pointing update.
/// </summary>
public class PrinterCatalogSeederSqlServerTests
{
    private static readonly string? Connection = Environment.GetEnvironmentVariable("PRINTLOG_SQLSERVER_TEST_CONNECTION");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SeedsTheEmbeddedCatalog_IsIdempotent_AndRepointsMergedIds()
    {
        Assert.SkipUnless(Connection is not null, "Set PRINTLOG_SQLSERVER_TEST_CONNECTION to run against SQL Server.");
        var connection = new SqlConnectionStringBuilder(Connection) { InitialCatalog = $"PrinterCatalogSeed_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlServer(connection, sql => sql.EnableRetryOnFailure())
            .Options;
        var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var embedded = PrinterCatalogFileLoader.LoadEmbedded();

        await using (var setup = new PrintLogContext(options))
        {
            await setup.Database.MigrateAsync(Ct);
        }
        try
        {
            await using (var db = new PrintLogContext(options))
            {
                var first = await PrinterCatalogSeeder.SeedAsync(db, embedded, now, Ct);
                Assert.Equal(embedded.Models.Count, first.Inserted);
            }

            // A printer on a model that the next file merges into another.
            var merged = embedded.Models[0].Id;
            var successor = embedded.Models[1].Id;
            long printerId;
            await using (var db = new PrintLogContext(options))
            {
                var printer = new Printer { User = new User { OAuthUserId = "sql-catalog" }, Name = "p", IsActive = true, CatalogModelId = merged };
                db.Printers.Add(printer);
                await db.SaveChangesAsync(Ct);
                printerId = printer.Id;
            }

            var next = embedded with
            {
                Version = new string(embedded.Version.Reverse().ToArray()),
                Models = embedded.Models.Skip(1).ToList(),
                Reserved = [.. embedded.Reserved, new ReservedId { Id = merged, MergedInto = successor }],
            };
            await using (var db = new PrintLogContext(options))
            {
                var second = await PrinterCatalogSeeder.SeedAsync(db, next, now.AddDays(1), Ct);
                Assert.Equal(new SeedResult(0, 0, 1, embedded.Models.Count - 1, 1), second);
            }

            await using (var db = new PrintLogContext(options))
            {
                Assert.Equal(successor, (await db.Printers.AsNoTracking().SingleAsync(p => p.Id == printerId, Ct)).CatalogModelId);
                Assert.True((await db.PrinterModels.AsNoTracking().SingleAsync(m => m.Id == merged, Ct)).Retired);
                Assert.Equal(next.Version, await db.PrinterCatalogVersions.Select(v => v.Version).SingleAsync(Ct));
            }
        }
        finally
        {
            await using var teardown = new PrintLogContext(options);
            await teardown.Database.EnsureDeletedAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TwoSeedsAtOnce_RunOneAfterTheOther()
    {
        Assert.SkipUnless(Connection is not null, "Set PRINTLOG_SQLSERVER_TEST_CONNECTION to run against SQL Server.");
        var connection = new SqlConnectionStringBuilder(Connection) { InitialCatalog = $"PrinterCatalogSeed_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<PrintLogContext>().UseSqlServer(connection).Options;
        var embedded = PrinterCatalogFileLoader.LoadEmbedded();
        var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

        await using (var setup = new PrintLogContext(options))
        {
            await setup.Database.MigrateAsync(Ct);
        }
        try
        {
            async Task<SeedResult> Seed()
            {
                await using var db = new PrintLogContext(options);
                return await PrinterCatalogSeeder.SeedAsync(db, embedded, now, Ct);
            }

            // Without the applock both would read an empty table and the second insert would hit
            // the primary key; with it, the loser waits and then finds the winner's marker.
            var results = await Task.WhenAll(Seed(), Seed());

            Assert.Contains(new SeedResult(embedded.Models.Count, 0, 0, 0, 0), results);
            Assert.Contains(new SeedResult(0, 0, 0, embedded.Models.Count, 0), results);
            await using var check = new PrintLogContext(options);
            Assert.Equal(embedded.Models.Count, await check.PrinterModels.CountAsync(Ct));
            Assert.Equal(embedded.GeneratedAt, (await check.PrinterCatalogVersions.SingleAsync(Ct)).CatalogGeneratedAt);
        }
        finally
        {
            await using var teardown = new PrintLogContext(options);
            await teardown.Database.EnsureDeletedAsync(CancellationToken.None);
        }
    }
}
