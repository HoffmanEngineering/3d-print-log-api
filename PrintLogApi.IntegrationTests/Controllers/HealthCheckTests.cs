using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
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

            var checks = root.GetProperty("checks").EnumerateArray();
            var databaseCheck = Assert.Single(checks, c => c.GetProperty("name").GetString() == "database");
            Assert.Equal("Healthy", databaseCheck.GetProperty("status").GetString());
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
