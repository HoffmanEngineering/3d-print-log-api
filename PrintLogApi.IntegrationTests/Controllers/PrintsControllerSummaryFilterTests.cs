using System.Net;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

public class PrintsControllerSummaryFilterTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _httpClient;

    public PrintsControllerSummaryFilterTests(CustomWebApplicationFactory factory) =>
        _httpClient = factory.CreateClient();

    /// <summary>
    /// Round-trip ("O") formatting renders the offset as "+00:00", and a bare '+' in a query
    /// string decodes to a space — which fails DateTimeOffset binding and 400s. Encode it.
    /// </summary>
    private static string Q(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("O"));

    private async Task<PagedList<PrintSummaryDTO>> Get(string query)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Prints/summary?{query}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PagedList<PrintSummaryDTO>>())!;
    }

    [Fact]
    public async Task Summary_FiltersByHalfOpenDateRange()
    {
        var all = await Get("pageNumber=1&pageSize=100");
        var dated = all.Items.Where(i => i.StartDate.HasValue).ToList();

        // Assert, don't early-return: an empty seeder would make this test pass vacuously
        // while proving nothing. IntegrationTestSeeder does seed dated prints.
        Assert.NotEmpty(dated);

        var pivot = dated.Min(i => i.StartDate)!.Value;

        var inclusive = await Get($"pageNumber=1&pageSize=100&fromDate={Q(pivot)}&toDate={Q(pivot.AddSeconds(1))}");
        var exclusive = await Get($"pageNumber=1&pageSize=100&fromDate={Q(pivot.AddSeconds(1))}&toDate={Q(pivot.AddSeconds(2))}");

        Assert.Contains(inclusive.Items, i => i.StartDate == pivot);
        Assert.DoesNotContain(exclusive.Items, i => i.StartDate == pivot);
    }

    [Fact]
    public async Task Summary_AcceptsMultipleStatuses()
    {
        var result = await Get("pageNumber=1&pageSize=100&filterByStatuses=Success&filterByStatuses=Failed");

        // Assert.All over an empty list passes and proves nothing — the filter could be
        // matching NOTHING rather than matching correctly.
        Assert.NotEmpty(result.Items);
        Assert.All(result.Items, i =>
            Assert.True(i.Status == Print.PrintStatus.Success || i.Status == Print.PrintStatus.Failed));
    }

    [Fact]
    public async Task Summary_LegacyScalarStatusStillWorks()
    {
        var result = await Get("pageNumber=1&pageSize=100&filterByStatus=Success");

        Assert.NotEmpty(result.Items);
        Assert.All(result.Items, i => Assert.Equal(Print.PrintStatus.Success, i.Status));
    }

    [Theory]
    [InlineData("fromDate=2026-01-01T00%3A00%3A00Z")]
    [InlineData("toDate=2026-01-01T00%3A00%3A00Z")]
    public async Task Summary_OneSidedRangeIsRejected_NotSilentlyIgnored(string rangeParam)
    {
        // Applying nothing and returning every print looks identical to "your filter ran
        // and matched everything", which is a wrong answer rather than a missing feature.
        var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/Prints/summary?pageNumber=1&pageSize=10&{rangeParam}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Summary_InvertedRangeReturnsBadRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/Prints/summary?pageNumber=1&pageSize=10&fromDate=2026-07-01T00:00:00Z&toDate=2026-06-01T00:00:00Z");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Summary_DifferentDateRanges_DoNotShareACacheEntry()
    {
        // The regression test for cache poisoning: adding a filter without adding it to the
        // cache key makes two different queries collide, and this endpoint is [AllowAnonymous],
        // so a poisoned entry is served to any viewer sharing the key.
        var all = await Get("pageNumber=1&pageSize=100");
        var dated = all.Items.Where(i => i.StartDate.HasValue).ToList();
        Assert.NotEmpty(dated); // the seeder must have dated prints for this to mean anything

        var pivot = dated.Min(i => i.StartDate)!.Value;
        var wide = $"fromDate={Q(pivot.AddDays(-1))}&toDate={Q(pivot.AddDays(1))}";
        var empty = $"fromDate={Q(pivot.AddYears(-50))}&toDate={Q(pivot.AddYears(-49))}";

        // Warm the cache with the wide range FIRST, then ask for a window containing nothing.
        var warm = await Get($"pageNumber=1&pageSize=100&{wide}");
        Assert.NotEmpty(warm.Items);

        var cold = await Get($"pageNumber=1&pageSize=100&{empty}");
        Assert.Empty(cold.Items); // a shared key would return the warm result here
    }
}
