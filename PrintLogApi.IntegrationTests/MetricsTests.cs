using System.Net;
using System.Text.Json;
using Xunit;

namespace PrintLogApi.IntegrationTests;

/// <summary>
/// The anonymous Prometheus <c>/metrics</c> endpoint was removed: Application Insights carries the
/// API's telemetry. The path must now answer exactly as any other unknown route does.
/// </summary>
public class MetricsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _httpClient;

    public MetricsTests(CustomWebApplicationFactory factory)
    {
        // No credentials of any kind: this is what an anonymous scraper sees.
        _httpClient = factory.CreateDefaultClient();
    }

    [Fact]
    public async Task Metrics_AnswersLikeAnUnknownRoute()
    {
        var ct = TestContext.Current.CancellationToken;

        var metrics = await _httpClient.GetAsync("/metrics", ct);
        var unknown = await _httpClient.GetAsync("/nope-xyz", ct);

        Assert.Equal(HttpStatusCode.NotFound, metrics.StatusCode);
        Assert.Equal(unknown.StatusCode, metrics.StatusCode);
        Assert.Equal(unknown.Content.Headers.ContentType?.MediaType, metrics.Content.Headers.ContentType?.MediaType);

        using var metricsBody = JsonDocument.Parse(await metrics.Content.ReadAsStringAsync(ct));
        using var unknownBody = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync(ct));
        foreach (var member in new[] { "type", "title", "status", "detail" })
        {
            Assert.Equal(unknownBody.RootElement.GetProperty(member).ToString(), metricsBody.RootElement.GetProperty(member).ToString());
        }
    }
}
