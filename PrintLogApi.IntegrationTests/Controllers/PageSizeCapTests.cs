using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// A page is at most <see cref="PagedRequest.MaxPageSize"/> rows (#309). It was unbounded: an
/// oversized print summary page was cached as one entry over HybridCache's 1 MiB payload limit,
/// which HybridCache refuses, so that page was recomputed on every call. A larger request is
/// clamped rather than refused, and the response's paging says the size it got.
///
/// <para>The largest page any first-party client asks for is 200 (the web app's analytics filter
/// lists printers and spools that way), so the cap sits above it.</para>
/// </summary>
public class PageSizeCapTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<PagedList<T>> GetAsync<T>(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _client.SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PagedList<T>>(cancellationToken: Ct))!;
    }

    [Theory]
    [InlineData(1_000, 250)]
    [InlineData(int.MaxValue, 250)]
    [InlineData(250, 250)]
    [InlineData(200, 200)]
    [InlineData(10, 10)]
    public void PagedRequest_ClampsOnlyAboveTheCap(int requested, int expected)
    {
        Assert.Equal(250, PagedRequest.MaxPageSize);
        Assert.Equal(expected, new PagedRequest { PageSize = requested }.PageSize);
    }

    [Fact]
    public async Task PrintSummary_ClampsAnOversizedPage()
    {
        var page = await GetAsync<PrintSummaryDTO>("/api/Prints/summary?pageNumber=1&pageSize=100000");

        Assert.Equal(PagedRequest.MaxPageSize, page.Paging.PageSize);
        Assert.True(page.Items.Count <= PagedRequest.MaxPageSize);
    }

    [Fact]
    public async Task PrintSummary_KeepsTheLargestPageTheWebAppAsksFor()
    {
        var page = await GetAsync<PrintSummaryDTO>("/api/Prints/summary?pageNumber=1&pageSize=200");

        Assert.Equal(200, page.Paging.PageSize);
    }

    [Fact]
    public async Task GroupedFeed_ClampsAnOversizedPage()
    {
        var page = await GetAsync<GroupedFeedItemDto>("/api/Prints/grouped?pageNumber=1&pageSize=100000");

        Assert.Equal(PagedRequest.MaxPageSize, page.Paging.PageSize);
    }
}
