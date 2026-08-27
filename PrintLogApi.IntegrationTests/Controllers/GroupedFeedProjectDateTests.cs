using PrintLogApi.IntegrationTests.Support;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using Xunit;
using static PrintLogApi.IntegrationTests.Support.ProjectDateSeedHelpers;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Covers the grouped feed's project sort key, which is the defect the feature request
/// reported: projects sorted by their creation date, so a project whose prints all ran in
/// March sorted as if it happened today.
/// </summary>
/// <remarks>
/// Its own class so this fixture's projects stay out of the counts other feed tests assert on.
/// The mainline seeder still creates five standalone prints in this database, so every
/// assertion here filters by a seeded id — never by position, and never over the whole page.
/// </remarks>
public class GroupedFeedProjectDateTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static long TestUserId => IntegrationTestSeeder.TestUserId;

    private async Task<PagedList<GroupedFeedItemDto>> GetGroupedFeedAsync(
        string sortDirection = "desc", int pageNumber = 1, int pageSize = 100)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/Prints/grouped?pageNumber={pageNumber}&pageSize={pageSize}" +
            $"&sortBy=date&sortDirection={sortDirection}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PagedList<GroupedFeedItemDto>>(
            cancellationToken: TestContext.Current.CancellationToken))!;
    }

    private static int IndexOfProject(PagedList<GroupedFeedItemDto> feed, Guid projectId) =>
        feed.Items!.ToList().FindIndex(i => i.ProjectId == projectId);

    private static int IndexOfPrint(PagedList<GroupedFeedItemDto> feed, long printId) =>
        feed.Items!.ToList().FindIndex(i => i.Print != null && i.Print.Id == printId);

    [Fact]
    public async Task GroupedFeed_SortsProjectByEarliestPrintDate_NotCreatedDate()
    {
        // The project row is created NOW but its prints ran in March. A standalone print sits
        // in April. Sorted newest-first, the April print must come BEFORE the March project —
        // which is exactly backwards if the project sorts on its creation date.
        var projectId = await SeedProjectWithPrintsAsync(factory, TestUserId, $"feed-{Guid.NewGuid():N}");
        var aprilPrintId = await SeedStandalonePrintAsync(
            factory, TestUserId, "2026-04-01T10:00:00Z", $"april-{Guid.NewGuid():N}");

        var feed = await GetGroupedFeedAsync("desc");
        var printIndex = IndexOfPrint(feed, aprilPrintId);
        var projectIndex = IndexOfProject(feed, projectId);

        Assert.True(printIndex >= 0, "the April print is missing from the feed");
        Assert.True(projectIndex >= 0, "the project is missing from the feed");
        Assert.True(printIndex < projectIndex,
            $"expected the April print (index {printIndex}) before the March project (index {projectIndex})");
    }

    [Fact]
    public async Task GroupedFeed_AscendingOrder_PutsTheMarchProjectFirst()
    {
        var projectId = await SeedProjectWithPrintsAsync(factory, TestUserId, $"asc-{Guid.NewGuid():N}");
        var aprilPrintId = await SeedStandalonePrintAsync(
            factory, TestUserId, "2026-04-02T10:00:00Z", $"asc-april-{Guid.NewGuid():N}");

        var feed = await GetGroupedFeedAsync("asc");

        Assert.True(IndexOfProject(feed, projectId) < IndexOfPrint(feed, aprilPrintId));
    }

    [Fact]
    public async Task GroupedFeed_ProjectWithoutPrints_StillAppearsAtItsCreatedDate()
    {
        var projectId = await SeedProjectWithNoPrintsAsync(factory, TestUserId, $"nop-{Guid.NewGuid():N}");

        var feed = await GetGroupedFeedAsync();

        Assert.Contains(feed.Items!, i => i.ProjectId == projectId);
    }

    [Fact]
    public async Task GroupedFeed_StartDateOverride_WinsOverPrintDates()
    {
        var projectId = await SeedProjectWithPrintsAsync(factory, TestUserId, $"ovr-{Guid.NewGuid():N}");
        await SetStartOverrideAsync(factory, projectId, new DateOnly(2026, 1, 1));

        var feed = await GetGroupedFeedAsync();
        var item = Assert.Single(feed.Items!, i => i.ProjectId == projectId);

        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), item.SortDate);
    }

    [Fact]
    public async Task GroupedFeed_ProjectWhosePrintsHaveNullStartDates_FallsBackToCreatedDate()
    {
        var projectId = await SeedProjectWithUndatedPrintsAsync(factory, TestUserId, $"und-{Guid.NewGuid():N}");

        var feed = await GetGroupedFeedAsync();
        var item = Assert.Single(feed.Items!, i => i.ProjectId == projectId);

        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(item.SortDate.UtcDateTime));
    }

    [Fact]
    public async Task GroupedFeed_MixedOffsetPrintStarts_PicksTheTrueEarliest()
    {
        // 2026-03-02T01:00+09:00 is 2026-03-01T16:00Z — EARLIER than 2026-03-02T00:00Z, even
        // though its text form sorts later. A SQL MIN over DateTimeOffset on SQLite gets this
        // wrong, and CI would then disagree with SQL Server in production.
        var projectId = await SeedProjectWithMixedOffsetPrintsAsync(
            factory, TestUserId, $"mix-{Guid.NewGuid():N}");

        var feed = await GetGroupedFeedAsync();
        var item = Assert.Single(feed.Items!, i => i.ProjectId == projectId);

        Assert.Equal(
            DateTimeOffset.Parse("2026-03-02T01:00:00+09:00").UtcDateTime,
            item.SortDate.UtcDateTime);
    }

    [Fact]
    public async Task GroupedFeed_EqualDates_PaginateWithoutDuplicatesOrGaps()
    {
        // Five projects all pinned to the same day — the common case once date-only overrides
        // exist, and precisely the case an unstable sort breaks across page boundaries.
        var prefix = $"tie-{Guid.NewGuid():N}";
        var ids = await SeedProjectsPinnedToSameDayAsync(
            factory, TestUserId, prefix, 5, new DateOnly(2026, 6, 1));

        var seen = new List<Guid>();
        for (var page = 1; page <= 20; page++)
        {
            var result = await GetGroupedFeedAsync("desc", pageNumber: page, pageSize: 2);
            if (result.Items!.Count == 0) break;
            seen.AddRange(result.Items!.Where(i => i.ProjectId.HasValue).Select(i => i.ProjectId!.Value));
        }

        var ours = seen.Where(ids.Contains).ToList();
        Assert.Equal(5, ours.Distinct().Count());
        Assert.Equal(ours.Count, ours.Distinct().Count());
    }
}
