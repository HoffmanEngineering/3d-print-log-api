using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

public class CorsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public CorsTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Cors_ExposesRequestContextHeader()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("Origin", "https://www.3dprintlog.com");

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.True(response.Headers.TryGetValues("Access-Control-Expose-Headers", out var values));
        Assert.Contains("Request-Context", string.Join(",", values));
    }
}
