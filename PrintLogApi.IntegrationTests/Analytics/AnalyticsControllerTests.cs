using System.Net;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Analytics;
using PrintLogApi.Models.DTOs.Print;
using Xunit;

namespace PrintLogApi.IntegrationTests.Analytics;

/// <summary>
/// Uses the MCP-seeded factory because the foreign-printer test needs another user's printer
/// to exist. Every test here is read-only; the cache-invalidation test lives in its own class
/// with its own factory so the print it creates cannot perturb these counts.
/// </summary>
public class AnalyticsControllerTests : IClassFixture<Mcp.McpDataWebApplicationFactory>
{
    private readonly Mcp.McpDataWebApplicationFactory _factory;
    private readonly HttpClient _httpClient;

    public AnalyticsControllerTests(Mcp.McpDataWebApplicationFactory factory)
    {
        _factory = factory;
        _httpClient = factory.CreateClient();
    }

    private static HttpRequestMessage Authed(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        return request;
    }

    [Fact]
    public async Task Overview_Unauthenticated_ReturnsUnauthorized()
    {
        var response = await _httpClient.GetAsync("/api/analytics/overview?timeZone=UTC");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Overview_Authenticated_ReturnsSuccess()
    {
        var response = await _httpClient.SendAsync(Authed("/api/analytics/overview?timeZone=UTC"));
        response.EnsureSuccessStatusCode();

        var body = (await response.Content.ReadFromJsonAsync<OverviewResponse>())!;
        Assert.NotNull(body);
        Assert.NotNull(body.Tiles);
        Assert.NotEqual("Auto", body.Granularity);
    }

    [Fact]
    public async Task GetActivity_RejectsAnUnauthenticatedRequest()
    {
        var response = await _httpClient.GetAsync("/api/analytics/activity?timeZone=UTC");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetActivity_RejectsAnInvertedRange()
    {
        var response = await _httpClient.SendAsync(Authed(
            "/api/analytics/activity?timeZone=UTC&fromDate=2026-07-01T00:00:00Z&toDate=2026-06-01T00:00:00Z"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Overview_InvertedRange_ReturnsBadRequest()
    {
        var response = await _httpClient.SendAsync(Authed(
            "/api/analytics/overview?timeZone=UTC&fromDate=2026-07-01T00:00:00Z&toDate=2026-06-01T00:00:00Z"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Overview_UnknownTimeZone_ReturnsBadRequest()
    {
        var response = await _httpClient.SendAsync(Authed("/api/analytics/overview?timeZone=Mars/Olympus_Mons"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Overview_TooManyPrinterIds_ReturnsBadRequest()
    {
        var ids = string.Join("&", Enumerable.Range(1, 51).Select(i => $"printerIds={i}"));
        var response = await _httpClient.SendAsync(Authed($"/api/analytics/overview?timeZone=UTC&{ids}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Overview_DoesNotAcceptACallerSuppliedUserId()
    {
        // A userId parameter must have no effect — the tenant comes from the token only.
        var mine = await _httpClient.SendAsync(Authed("/api/analytics/overview?timeZone=UTC"));
        var spoofed = await _httpClient.SendAsync(Authed("/api/analytics/overview?timeZone=UTC&userId=999999"));

        var a = (await mine.Content.ReadFromJsonAsync<OverviewResponse>())!;
        var b = (await spoofed.Content.ReadFromJsonAsync<OverviewResponse>())!;

        Assert.Equal(a.Tiles.PrintCount.Value, b.Tiles.PrintCount.Value);
    }

    [Fact]
    public async Task Overview_ForeignPrinterId_MatchesNothing_AndIsNotAnError()
    {
        // MetricsPrinterId belongs to MetricsUserId and carries four of that user's prints.
        // Filtering by it must return a valid, empty response — never a 403/404 (which would
        // confirm the id exists) and never the owner's prints.
        //
        // Deliberately NOT OtherPrinterId: the caller owns a print that REFERENCES that
        // printer (the cross-owner fixture), so a non-zero count there is correct behaviour
        // and would make this assertion meaningless.
        _ = _factory.Services; // force seeding before reading the static id
        var response = await _httpClient.SendAsync(Authed(
            $"/api/analytics/overview?timeZone=UTC&printerIds={Mcp.McpTestData.MetricsPrinterId}"));
        response.EnsureSuccessStatusCode();

        var body = (await response.Content.ReadFromJsonAsync<OverviewResponse>())!;
        Assert.Equal(0, body!.Tiles.PrintCount.Value);
    }
}

/// <summary>
/// Isolated: this class creates a print, so it must not share a database with assertions on
/// exact counts.
/// </summary>
public class AnalyticsCacheInvalidationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _httpClient;

    public AnalyticsCacheInvalidationTests(CustomWebApplicationFactory factory) =>
        _httpClient = factory.CreateClient();

    private static HttpRequestMessage Authed(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        return request;
    }

    [Fact]
    public async Task Overview_AfterAMutation_DoesNotServeTheStaleCachedResult()
    {
        var before = (await (await _httpClient.SendAsync(Authed("/api/analytics/overview?timeZone=UTC")))
            .Content.ReadFromJsonAsync<OverviewResponse>())!;

        // Any mutating Prints action bumps the user's cache version. Create a print through
        // the API (not the DbContext) so the real invalidation path runs. A TTL-only cache
        // would still be serving `before` here.
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/Prints")
        {
            Content = JsonContent.Create(new AddPrintDTO
            {
                Title = "cache invalidation probe",
                PrinterId = IntegrationTestSeeder.TestPrinterId,
                Status = Print.PrintStatus.Success,
                ViewStatus = Print.PrintViewStatus.Private,
            }),
        };
        create.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        (await _httpClient.SendAsync(create)).EnsureSuccessStatusCode();

        var after = (await (await _httpClient.SendAsync(Authed("/api/analytics/overview?timeZone=UTC")))
            .Content.ReadFromJsonAsync<OverviewResponse>())!;

        Assert.Equal(before!.Tiles.PrintCount.Value + 1, after!.Tiles.PrintCount.Value);
    }
}
