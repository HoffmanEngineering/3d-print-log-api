using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers
{
    /// <summary>
    /// Covers the liveness and readiness endpoints.
    ///
    /// The split between them is the point: App Service polls /health and replaces instances that
    /// keep failing it, so /health must not depend on the database, while /health/ready must.
    /// </summary>
    public class HealthCheckTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly CustomWebApplicationFactory _factory;

        public HealthCheckTests(CustomWebApplicationFactory factory) => _factory = factory;

        [Fact]
        public async Task Liveness_IsHealthy_AndAnonymous()
        {
            var client = _factory.CreateClient();

            // No authentication header at all — App Service polls this unauthenticated.
            var response = await client.GetAsync("/health");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task Readiness_IsHealthy_AndReportsTheDatabaseCheck()
        {
            var client = _factory.CreateClient();

            var response = await client.GetAsync("/health/ready");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            Assert.Equal("Healthy", root.GetProperty("status").GetString());

            // Exactly one check, and it is the database one. Asserting only that a "database"
            // entry exists would not notice an unrelated check leaking into the readiness set.
            var checks = root.GetProperty("checks").EnumerateArray().ToList();
            var databaseCheck = Assert.Single(checks);
            Assert.Equal("database", databaseCheck.GetProperty("name").GetString());
            Assert.Equal("Healthy", databaseCheck.GetProperty("status").GetString());
        }

        /// <summary>
        /// Registers a readiness check that fails with an exception carrying a connection-string
        /// shaped message, so the failure path can be exercised. The real failure — SQL Server
        /// being unreachable — cannot be reproduced against in-memory SQLite, but the response
        /// writer does not care why a check failed, only what it discloses.
        /// </summary>
        public sealed class FailingReadinessFactory : CustomWebApplicationFactory
        {
            public const string SecretDetail =
                "Server=prod-sql.database.windows.net;User ID=admin;Password=hunter2";

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                base.ConfigureWebHost(builder);

                // A distinct name: the real "database" registration cannot be duplicated, and it
                // does not need to be — the report takes the worst status, so one failing check
                // is enough to drive the response down the unhealthy path.
                builder.ConfigureServices(services =>
                    services.AddHealthChecks().AddCheck(
                        "failing-dependency",
                        () => HealthCheckResult.Unhealthy(
                            description: SecretDetail,
                            exception: new System.InvalidOperationException(SecretDetail)),
                        tags: new[] { "ready" }));
            }
        }

        [Fact]
        public async Task Readiness_WhenUnhealthy_Returns503_AndLeaksNoDetail()
        {
            using var factory = new FailingReadinessFactory();
            var client = factory.CreateClient();

            var response = await client.GetAsync("/health/ready");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

            using var document = JsonDocument.Parse(body);
            Assert.Equal("Unhealthy", document.RootElement.GetProperty("status").GetString());

            // The endpoint is anonymous, so a failing check's exception and description must never
            // reach the caller — a real SQL failure message carries the server name and often the
            // credentials it tried.
            Assert.DoesNotContain("hunter2", body);
            Assert.DoesNotContain("prod-sql", body);
            Assert.DoesNotContain("InvalidOperationException", body);
        }

        [Fact]
        public async Task Liveness_DoesNotRunTheDatabaseCheck()
        {
            // Asserting on the HTTP body cannot show this: /health returns a bare "Healthy" whether
            // or not the database check ran, and the two only diverge when SQL is actually down —
            // which this suite cannot reproduce against in-memory SQLite. So run the same tag
            // predicate the endpoint uses and assert on which checks it selected.
            //
            // This is the guard on the whole live/ready split: if the database check ever acquires
            // the "live" tag, a SQL outage starts failing liveness, and App Service responds by
            // replacing every instance of a perfectly healthy app.
            _ = _factory.CreateClient();

            using var scope = _factory.Services.CreateScope();
            var healthCheckService = scope.ServiceProvider.GetRequiredService<HealthCheckService>();

            var report = await healthCheckService.CheckHealthAsync(
                registration => registration.Tags.Contains("live"));

            Assert.Equal(HealthStatus.Healthy, report.Status);
            Assert.Equal(new[] { "self" }, report.Entries.Keys);
        }
    }
}
