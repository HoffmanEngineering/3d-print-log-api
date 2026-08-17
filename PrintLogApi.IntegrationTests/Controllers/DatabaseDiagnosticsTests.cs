using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Tests verifying database seeding and API data consistency.
/// </summary>
public class DatabaseDiagnosticsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _httpClient;

    public DatabaseDiagnosticsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _httpClient = factory.CreateClient();
    }

    [Fact]
    public void Database_HasSeededData()
    {
        // Query database directly
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        // Verify seeded data exists
        Assert.Equal(1, db.Users.Count());
        Assert.Equal(2, db.Printers.Count());
        Assert.Equal(4, db.Filaments.Count());
        Assert.Equal(5, db.Prints.Count());

        // Verify all prints belong to the test user
        var prints = db.Prints.ToList();
        Assert.All(prints, p => Assert.Equal(IntegrationTestSeeder.TestUserId, p.CreatedById));
    }

    [Fact]
    public async Task Api_ReturnsCorrectTotalCount()
    {
        // Make authenticated API call
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Prints/summary");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var model = (await response.Content.ReadFromJsonAsync<PagedList<PrintSummaryDTO>>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Verify counts match
        Assert.NotNull(model);
        Assert.Equal(5, model.Paging.TotalCount);
        Assert.Equal(5, model.Items.Count);
    }
}
