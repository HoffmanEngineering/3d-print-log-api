using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PrintLogApi.Achievements.Triggers;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// The part of #232 SQLite cannot vouch for. A density-0 spool's weight adjustment once had its
/// volume derived by dividing by zero; SQLite stores the resulting Infinity, but SQL Server's
/// float column rejects it, so the create and every later save (the web app re-sends each
/// adjustment) returned 500.
///
/// Skipped unless PRINTLOG_SQLSERVER_TEST_CONNECTION points at a server (docker-compose.yml runs
/// one); the database named in it is ignored, and a uniquely named one is created and dropped.
/// </summary>
public class FilamentAdjustmentSqlServerTests(FilamentAdjustmentSqlServerTests.SqlServerFactory factory)
    : IClassFixture<FilamentAdjustmentSqlServerTests.SqlServerFactory>
{
    private static readonly string? Connection = Environment.GetEnvironmentVariable("PRINTLOG_SQLSERVER_TEST_CONNECTION");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ZeroDensitySpool_WeightAdjustment_SavesOnCreateAndOnLaterEdits()
    {
        Assert.SkipUnless(Connection is not null, "Set PRINTLOG_SQLSERVER_TEST_CONNECTION to run against SQL Server.");
        var client = factory.CreateClient();

        var create = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments");
        create.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        create.Content = JsonContent.Create(new AddFilamentDto
        {
            DisplayName = "Zero Density SQL Server 232",
            MaterialType = "PLA",
            MaterialDensityGramPerCubicCm = 0,
            InitialNominalWeightMg = 1_000_000,
            IsActive = true,
            FilamentAdjustments =
            [
                new FilamentAdjustmentDto { Source = FilamentAdjustment.SourceMeasurement.Weight, AmountMg = -50_000 },
            ],
        });
        var created = await client.SendAsync(create, Ct);
        var createBody = await created.Content.ReadAsStringAsync(Ct);
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"Create: {(int)created.StatusCode} {createBody}");
        var filament = (await created.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: Ct))!;

        // A rename, re-sending the stored adjustment as the web app does.
        filament.DisplayName = "Zero Density SQL Server 232 Renamed";
        var update = new HttpRequestMessage(HttpMethod.Put, $"/api/Filaments/{filament.Id}");
        update.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        update.Content = JsonContent.Create(filament);
        var updated = await client.SendAsync(update, Ct);
        var updateBody = await updated.Content.ReadAsStringAsync(Ct);
        Assert.True(updated.IsSuccessStatusCode, $"Update: {(int)updated.StatusCode} {updateBody}");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var row = await db.FilamentAdjustments.AsNoTracking().SingleAsync(a => a.FilamentId == filament.Id, Ct);
        Assert.Equal(-50_000, row.AmountMg);
        Assert.Null(row.VolumeMl);
        var stored = await db.Filaments.AsNoTracking().SingleAsync(f => f.Id == filament.Id, Ct);
        Assert.Null(stored.InitialNominalVolumeMl);
    }

    /// <summary>The standard test host, schema and seed on a scratch SQL Server database.</summary>
    public sealed class SqlServerFactory : CustomWebApplicationFactory
    {
        private readonly string? _connection = Connection is null
            ? null
            : new SqlConnectionStringBuilder(Connection) { InitialCatalog = $"FilamentAdjustment_{Guid.NewGuid():N}" }.ConnectionString;

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

            // Migrations, not EnsureCreated: the model's convention cascades are refused by SQL
            // Server (error 1785), and the migrations are what hand-edit them away.
            db.Database.Migrate();
            IntegrationTestSeeder.Seed(db);
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
