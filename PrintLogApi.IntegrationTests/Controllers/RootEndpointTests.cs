using System.Net;
using System.Text.Json;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// App Service's Always On pings <c>GET /</c> to keep the site warm. Before the root was mapped
/// every ping was a 404, about 300 a day in Application Insights' failed requests.
/// </summary>
public class RootEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public RootEndpointTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Root_IsOk_AndAnonymous()
    {
        var client = _factory.CreateClient();

        // No authentication header: Always On sends none.
        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Root_IsNotInTheOpenApiDocument()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.False(document.RootElement.GetProperty("paths").TryGetProperty("/", out _));
    }
}
