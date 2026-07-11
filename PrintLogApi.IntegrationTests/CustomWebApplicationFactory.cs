using System.Linq;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PrintLogApi.IntegrationTests.Services;
using PrintLogApi.Services;
using PrintLogApi.Services.Billing;

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
        private IStripeGateway _stripeGatewayOverride;

        /// <summary>
        /// Captures telemetry emitted during a test so assertions can observe events
        /// such as Subscription_DuplicateActiveDetected.
        /// </summary>
        public TestTelemetryChannel TelemetryChannel { get; } = new();

        /// <summary>
        /// Swaps in a fake Stripe gateway. Must be called before the first CreateClient()/CreateHost().
        /// </summary>
        public CustomWebApplicationFactory WithStripeGateway(IStripeGateway gateway)
        {
            _stripeGatewayOverride = gateway;
            return this;
        }

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

                var auth0Descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IAuth0Service));
                if (auth0Descriptor != null)
                {
                    services.Remove(auth0Descriptor);
                }
                services.AddSingleton<IAuth0Service, TestAuth0Service>();

                if (_stripeGatewayOverride != null)
                {
                    var stripeDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IStripeGateway));
                    if (stripeDescriptor != null)
                    {
                        services.Remove(stripeDescriptor);
                    }
                    services.AddSingleton(_stripeGatewayOverride);
                }

                var channelDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(ITelemetryChannel));
                if (channelDescriptor != null)
                {
                    services.Remove(channelDescriptor);
                }
                services.AddSingleton<ITelemetryChannel>(TelemetryChannel);

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

        private class TestAuth0Service : IAuth0Service
        {
            public Task DeleteUser(string oauthUserId)
            {
                return Task.CompletedTask;
            }

            public Task<string> GetManagementApiBearerToken()
            {
                return Task.FromResult("test-token");
            }

            public Task GetUser(string oauthUserId)
            {
                return Task.CompletedTask;
            }
        }
    }
}
