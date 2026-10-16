using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PrintLogApi.Achievements.Triggers;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// <see cref="SpoolFiguresConsistencyTestsBase"/> against a real SQL Server, on a scratch database
/// built from the migrations. Every figure there is a correlated sum over usage rows that reads
/// density and diameter through <c>pf.Filament</c>; SQLite runs any shape of that, SQL Server
/// refuses a sum that reads more than one outer column (error 8124), so only this host can prove
/// the converted queries run in production.
///
/// Skipped unless PRINTLOG_SQLSERVER_TEST_CONNECTION points at a server (docker-compose.yml runs
/// one); the database named in it is ignored, and a uniquely named one is created and dropped.
/// </summary>
public class SpoolFiguresSqlServerTests(SpoolFiguresSqlServerTests.SqlServerFactory factory)
    : SpoolFiguresConsistencyTestsBase(factory), IClassFixture<SpoolFiguresSqlServerTests.SqlServerFactory>
{
    private static readonly string? Connection = Environment.GetEnvironmentVariable("PRINTLOG_SQLSERVER_TEST_CONNECTION");

    protected override string? SkipReason => Connection is null
        ? "Set PRINTLOG_SQLSERVER_TEST_CONNECTION to run against SQL Server."
        : null;

    /// <summary>The standard test host on a scratch SQL Server database, migrated rather than created.</summary>
    public sealed class SqlServerFactory : CustomWebApplicationFactory
    {
        private readonly string? _connection = Connection is null
            ? null
            : new SqlConnectionStringBuilder(Connection) { InitialCatalog = $"SpoolFigures_{Guid.NewGuid():N}" }.ConnectionString;

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

        /// <summary>
        /// Stops the host before dropping the database, so its background services are not
        /// holding connections the drop has to roll back; that wait outlasted the default command
        /// timeout.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing && _connection is not null)
            {
                var name = new SqlConnectionStringBuilder(_connection).InitialCatalog;
                var master = new SqlConnectionStringBuilder(_connection) { InitialCatalog = "master" }.ConnectionString;
                SqlConnection.ClearAllPools();
                using var conn = new SqlConnection(master);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"IF DB_ID('{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END";
                cmd.CommandTimeout = 120;
                cmd.ExecuteNonQuery();
            }
        }
    }
}
