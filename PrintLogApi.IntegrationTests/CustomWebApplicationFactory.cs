using System.Linq;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PrintLogApi.Services;

namespace PrintLogApi.IntegrationTests
{
    /// <summary>
    /// Custom WebApplicationFactory that configures the application for integration testing.
    /// Uses SQLite in-memory database, test authentication, and seeds test data.
    /// </summary>
    public class CustomWebApplicationFactory : WebApplicationFactory<Startup>
    {
        private SqliteConnection _connection;
        private bool _seeded = false;

        public CustomWebApplicationFactory()
        {
            // Create and open the connection early - it must stay open for the entire test lifetime
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("IntegrationTesting");

            builder.ConfigureServices(services =>
            {
                // Remove ALL DbContext-related registrations
                var descriptorsToRemove = services.Where(
                    d => d.ServiceType == typeof(DbContextOptions<PrintLogContext>) ||
                         d.ServiceType == typeof(DbContextOptions) ||
                         d.ServiceType == typeof(PrintLogContext) ||
                         d.ServiceType.FullName?.Contains("EntityFrameworkCore") == true
                ).ToList();

                foreach (var descriptor in descriptorsToRemove)
                {
                    services.Remove(descriptor);
                }

                // Add DbContext using the shared SQLite connection
                services.AddDbContext<PrintLogContext>((sp, options) =>
                {
                    options.UseSqlite(_connection);
                    options.ConfigureWarnings(warnings =>
                        warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
                });

                // Replace IBlobStorageService with in-memory implementation for testing
                var blobDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IBlobStorageService));
                if (blobDescriptor != null)
                {
                    services.Remove(blobDescriptor);
                }
                services.AddSingleton<IBlobStorageService, InMemoryBlobStorageService>();

                // Add test authentication scheme
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.AuthenticationScheme;
                    options.DefaultChallengeScheme = TestAuthHandler.AuthenticationScheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.AuthenticationScheme, options => { });
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);

            // Seed the database using the actual host's service provider
            if (!_seeded)
            {
                using (var scope = host.Services.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                    db.Database.EnsureCreated();
                    IntegrationTestSeeder.Seed(db);
                }
                _seeded = true;
            }

            return host;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _connection?.Close();
                _connection?.Dispose();
            }
        }
    }
}
