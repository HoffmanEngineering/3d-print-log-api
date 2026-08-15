using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using PrintLogApi.IntegrationTests.Mcp;
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

        /// <summary>The address feedback notifications are sent to under test.</summary>
        public const string TestFeedbackEmailAddress = "feedback-inbox@example.test";

        /// <summary>The recording email sender, for asserting on notifications the app sent.</summary>
        public RecordingEmailSender EmailSender => (RecordingEmailSender)Services.GetRequiredService<IEmailSender>();

        /// <summary>The stub Auth0 service, for controlling the account-email lookup.</summary>
        public TestAuth0Service Auth0 => (TestAuth0Service)Services.GetRequiredService<IAuth0Service>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("IntegrationTesting");

            // A feedback notification is only composed when a recipient is configured, and the test
            // settings leave it empty — without this the whole notification path would be skipped and
            // the tests covering it would pass vacuously.
            //
            // Must be ConfigureAppConfiguration, not UseSetting: UseSetting writes host configuration,
            // which appsettings.json is then layered ON TOP of, putting the empty value back.
            builder.ConfigureAppConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FeedbackEmailAddress"] = TestFeedbackEmailAddress,
                }));

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

                var emailDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IEmailSender));
                if (emailDescriptor != null)
                {
                    services.Remove(emailDescriptor);
                }
                services.AddSingleton<IEmailSender, RecordingEmailSender>();

                // Add test authentication scheme
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.AuthenticationScheme;
                    options.DefaultChallengeScheme = TestAuthHandler.AuthenticationScheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.AuthenticationScheme, options => { });

                // Validate the real Bearer / McpBearer schemes against a local signing key so
                // MCP audience isolation can be exercised without contacting Auth0.
                services.PostConfigure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    o => ConfigureLocalJwt(o, TestJwt.ApiAudience));
                services.PostConfigure<JwtBearerOptions>(
                    "McpBearer",
                    o => ConfigureLocalJwt(o, TestJwt.McpAudience));

                // Map the MCP auth probe endpoint (test-only) guarded by the McpAccess policy.
                services.AddSingleton<IStartupFilter, McpAuthProbeStartupFilter>();
            });
        }

        private static void ConfigureLocalJwt(JwtBearerOptions options, string audience)
        {
            options.Authority = null;
            options.MetadataAddress = null;
            options.RequireHttpsMetadata = false;
            // The built-in post-configure already created a ConfigurationManager from Authority;
            // clear it so validation uses the in-memory signing key instead of a (slow, failing)
            // OIDC metadata fetch to a non-existent test tenant.
            options.ConfigurationManager = null;
            options.Configuration = null;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = TestJwt.Issuer,
                ValidateAudience = true,
                ValidAudience = audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = TestJwt.SigningKey,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero,
                NameClaimType = ClaimTypes.Upn,
            };
        }

        /// <summary>
        /// Adds a test-only <c>/api/mcp-auth-probe</c> endpoint guarded by the McpAccess policy so
        /// tests can assert the policy is registered and enforced without touching production Startup.
        /// </summary>
        private sealed class McpAuthProbeStartupFilter : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                next(app);

                // A normal API-bearer probe: forces the default Bearer scheme (app audience only)
                // so tests can prove an MCP-audience token cannot call an ordinary endpoint.
                var webPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(
                        JwtBearerDefaults.AuthenticationScheme)
                    .RequireAuthenticatedUser()
                    .Build();

                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGet("/api/mcp-auth-probe", () => Results.Ok())
                        .RequireAuthorization("McpRead");
                    endpoints.MapGet("/api/web-auth-probe", () => Results.Ok())
                        .RequireAuthorization(webPolicy);
                });
            };
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

        public class TestAuth0Service : IAuth0Service
        {
            /// <summary>The address <see cref="GetUserEmail"/> returns. Null means the account has none.</summary>
            public string UserEmail { get; set; }

            /// <summary>When set, the lookup throws — for testing that callers degrade instead of failing.</summary>
            public bool ThrowOnGetUserEmail { get; set; }

            /// <summary>
            /// When set, the lookup throws <see cref="OperationCanceledException"/> — the shape a
            /// client disconnect or the 30s HttpClient timeout arrives in.
            /// </summary>
            public bool ThrowCancelledOnGetUserEmail { get; set; }

            public Task DeleteUser(string oauthUserId)
            {
                return Task.CompletedTask;
            }

            public Task<string> GetManagementApiBearerToken()
            {
                return Task.FromResult("test-token");
            }

            public Task<string> GetUserEmail(string oauthUserId, System.Threading.CancellationToken ct)
            {
                if (ThrowCancelledOnGetUserEmail)
                {
                    throw new OperationCanceledException("Simulated cancellation during Auth0 lookup.");
                }
                if (ThrowOnGetUserEmail)
                {
                    throw new PrintLogApi.Exceptions.Auth0ApiException("Simulated Auth0 failure.");
                }
                return Task.FromResult(UserEmail);
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
}
