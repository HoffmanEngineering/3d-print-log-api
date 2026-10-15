using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PrintLogApi.Achievements.Triggers;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// The parts of #174 that SQLite cannot vouch for, against a real SQL Server: the migration's
/// backfill, and spool numbers taken by creates that genuinely run in parallel. The rest of the
/// suite builds its schema with EnsureCreated, so it never runs a migration, and it shares one
/// SQLite connection, so its "concurrent" creates only interleave.
///
/// Skipped unless PRINTLOG_SQLSERVER_TEST_CONNECTION points at a server (docker-compose.yml runs
/// one); the database named in it is ignored, and a uniquely named one is created and dropped.
/// </summary>
public class SpoolNumberSqlServerTests(SpoolNumberSqlServerTests.SqlServerFactory factory)
    : IClassFixture<SpoolNumberSqlServerTests.SqlServerFactory>
{
    private const string MigrationBefore = "20261012033722_AddFilamentTags";

    private static readonly string? Connection = Environment.GetEnvironmentVariable("PRINTLOG_SQLSERVER_TEST_CONNECTION");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// One test, because the migration can only be applied once per database: the legacy rows
    /// have to exist before it runs, and everything after it needs it to have run.
    /// </summary>
    [Fact]
    public async Task Migration_BackfillsPerUser_AndParallelCreatesTakeDistinctNumbers()
    {
        Assert.SkipUnless(Connection is not null, "Set PRINTLOG_SQLSERVER_TEST_CONNECTION to run against SQL Server.");

        long alice, bob;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            await db.GetService<IMigrator>().MigrateAsync(MigrationBefore, Ct);

            alice = await InsertLegacyUserAsync(db, "alice");
            bob = await InsertLegacyUserAsync(db, "bob");
            // Inserted out of date order, and two with the same date, to pin the ordering rule.
            await InsertLegacyFilamentAsync(db, alice, "a-third", new DateTime(2024, 3, 1), Guid.Parse("00000000-0000-0000-0000-000000000003"));
            await InsertLegacyFilamentAsync(db, alice, "a-first", new DateTime(2024, 1, 1), Guid.Parse("00000000-0000-0000-0000-000000000001"));
            await InsertLegacyFilamentAsync(db, alice, "a-second", new DateTime(2024, 1, 1), Guid.Parse("00000000-0000-0000-0000-000000000002"));
            await InsertLegacyFilamentAsync(db, bob, "b-first", new DateTime(2025, 1, 1), Guid.NewGuid());

            await db.Database.MigrateAsync(Ct);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var numbered = await db.Filaments
                .Where(f => f.CreatedById == alice || f.CreatedById == bob)
                .ToDictionaryAsync(f => f.DisplayName!, f => f.SpoolNumber, Ct);

            Assert.Equal(1, numbered["a-first"]);
            Assert.Equal(2, numbered["a-second"]);
            Assert.Equal(3, numbered["a-third"]);
            Assert.Equal(1, numbered["b-first"]);
            Assert.Equal(3, await db.Users.Where(u => u.Id == alice).Select(u => u.LastSpoolNumber).SingleAsync(Ct));
        }

        // Truly parallel: each on its own thread, scope and pooled connection.
        var created = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
        {
            using var scope = factory.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IFilamentService>();
            return (await service.AddFilament(new AddFilamentDto
            {
                DisplayName = $"Parallel {Guid.NewGuid():N}",
                MaterialType = "PLA",
                DiameterMm = 1.75,
                InitialNominalWeightMg = 1_000_000,
                MaterialDensityGramPerCubicCm = 1.24,
                IsActive = true,
            }, alice)).SpoolNumber!.Value;
        }, Ct)));

        Assert.Equal(Enumerable.Range(4, 12), created.Order());
    }

    private static async Task<long> InsertLegacyUserAsync(PrintLogContext db, string name)
    {
        // Raw SQL: the model already has the new columns, so EF would write to columns this
        // schema does not have yet.
        var ids = await db.Database.SqlQuery<long>($"""
            INSERT INTO Users (OAuthUserId, DisplayName, ViewStatus, AchievementCatalogVersion, EmailVerified)
            OUTPUT INSERTED.Id AS Value
            VALUES ({"auth0|spool-sql-" + name + Guid.NewGuid().ToString("N")}, {name}, 4, 0, 0)
            """).ToListAsync(Ct);
        return ids.Single();
    }

    private static Task InsertLegacyFilamentAsync(PrintLogContext db, long userId, string name, DateTime created, Guid id) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO Filaments (Id, MaterialCategoryNickname, DisplayName, MaterialDensityGramPerCubicCm, Source, IsActive, IsFavorite, CreatedDate, CreatedById, UpdatedDate, UpdatedById)
            VALUES ({id}, 'filament', {name}, 1.24, 1, 1, 0, {created}, {userId}, {created}, {userId})
            """, Ct);

    /// <summary>The standard test host on a scratch SQL Server database, left unmigrated for the test to drive.</summary>
    public sealed class SqlServerFactory : CustomWebApplicationFactory
    {
        private readonly string? _connection = Connection is null
            ? null
            : new SqlConnectionStringBuilder(Connection) { InitialCatalog = $"SpoolNumber_{Guid.NewGuid():N}" }.ConnectionString;

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
            }

            // Otherwise left empty: the test migrates in two steps around its legacy rows.
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
