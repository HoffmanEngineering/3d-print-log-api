using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Subscription;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

public class SubscriptionControllerTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static HttpRequestMessage AuthenticatedRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        return request;
    }

    private async Task<SubscriptionDto> GetMeAsync(CancellationToken ct)
    {
        // The route is /api/Subscription/me - GetCurrentUserSubscription is [HttpGet("me")].
        // A test against /api/Subscription would 404 forever.
        var resp = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/api/Subscription/me"), ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<SubscriptionDto>(ct))!;
    }

    [Fact]
    public async Task GetCurrentUserSubscription_FreeUser_ReportsFiveMaxImages()
    {
        var dto = await GetMeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SubscriptionLimits.FreeMaxImages, dto.MaxImages);
        Assert.Equal(5, dto.MaxImages);
    }

    [Fact]
    public async Task GetCurrentUserSubscription_KeepsMaxImagesPerPrintForWireCompatibility()
    {
        var dto = await GetMeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(dto.MaxImages, dto.MaxImagesPerPrint);
    }
}
