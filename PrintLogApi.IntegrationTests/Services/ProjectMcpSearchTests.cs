using PrintLogApi.Models;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// MCP <c>list_projects</c> matches its search as plain text, as the REST project search does:
/// <c>%</c>, <c>_</c> and <c>[</c> in a search are characters to find, not LIKE wildcards.
/// </summary>
public class ProjectMcpSearchTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("50%", new[] { "50% infill" })]
    [InlineData("%", new[] { "50% infill", "Plain box" })]
    [InlineData("_", new[] { "a_b bracket" })]
    [InlineData("a_b", new[] { "a_b bracket" })]
    [InlineData("[a]", new[] { "[a] tray" })]
    [InlineData("  infill  ", new[] { "50% infill" })]
    [InlineData("REF-7%", new[] { "Plain box" })]
    public async Task Search_MatchesItsTextLiterally(string search, string[] expected)
    {
        var userId = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectService>();

        var page = await projects.ListProjectsForMcp(userId, 1, 100, search, null, Ct);

        Assert.Equal(expected, page.Items.Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(expected.Length, page.TotalCount);
    }

    private async Task<long> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = new User
        {
            OAuthUserId = $"auth0|mcp-search-{Guid.NewGuid():N}",
            DisplayName = "Search",
            Email = $"mcp-search-{Guid.NewGuid():N}@example.test",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);

        foreach (var (name, reference) in new (string, string?)[]
        {
            ("50% infill", null),
            ("500 mm tower", "REF-700"),
            ("a_b bracket", null),
            ("axb clip", null),
            ("[a] tray", null),
            ("a tray", null),
            ("Plain box", "REF-7%"),
        })
        {
            db.Projects.Add(new Project
            {
                Name = name,
                Reference = reference,
                Status = Project.ProjectStatus.InProgress,
                CreatedById = user.Id,
                UpdatedById = user.Id,
            });
        }
        await db.SaveChangesAsync(Ct);
        return user.Id;
    }
}
