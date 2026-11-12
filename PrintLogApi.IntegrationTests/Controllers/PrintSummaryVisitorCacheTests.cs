using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using PrintLogApi.Models;
using Xunit;
using static PrintLogApi.IntegrationTests.Controllers.PublicReadSeed;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// <c>GET api/Prints/summary</c> caches a reader who is not the owner only for the shapes the
/// public pages send (the profile's print list and the project page). Any other shape from such a
/// reader runs uncached, so varying a query value cannot fill the process-wide cache and evict
/// other users' entries. The owner's own requests cache as before.
/// </summary>
public class PrintSummaryVisitorCacheTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    /// <summary>The sort the web app always sends: start date, newest first.</summary>
    private const string DefaultSort = "SortColumn=2&SortDirection=2";

    private static string Q(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("O"));

    private List<string> SummaryKeys(long ownerId, string viewer)
    {
        var cache = (MemoryCache)factory.Services.GetRequiredService<IMemoryCache>();
        var prefix = $"print_summary_{ownerId}_viewer{viewer}_";
        return cache.Keys.OfType<string>().Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }

    private static string ViewerKey(Caller caller, Owner owner, User? other) => caller switch
    {
        Caller.Anonymous => "anon",
        Caller.OtherUser => other!.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => owner.User.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static List<long> Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64()).ToList();

    /// <summary>No usage row a reader who is not the owner receives carries the owner's spool data.</summary>
    private static void AssertRedacted(JsonElement page)
    {
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.NotEmpty(items);
        foreach (var item in items)
        {
            var usage = item.GetProperty("filamentUsage").GetRawText();
            foreach (var marker in PrivateMarkers)
            {
                Assert.DoesNotContain(marker, usage, StringComparison.Ordinal);
            }
        }
    }

    private async Task<(Owner Owner, List<long> PublicIds, Guid ProjectId)> SeedAsync()
    {
        var owner = await OwnerAsync(factory);
        var projectId = await ProjectAsync(factory, owner, Project.ProjectViewStatus.Public, "Visitor cache project");
        var first = await PrintAsync(factory, owner, start: new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), projectId: projectId);
        var second = await PrintAsync(factory, owner, start: new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero), projectId: projectId);
        await PrintAsync(factory, owner, PrintViewStatus.Private, start: new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero), title: "Private benchy");
        // Newest first, as the default sort returns them.
        return (owner, [second.Id, first.Id], projectId);
    }

    /// <summary>
    /// Shapes no public page sends. Each placeholder is filled from the seeded owner, so every
    /// filter still matches the public prints and the test can check the data, not just a 200.
    /// </summary>
    public static TheoryData<string, Caller> NonCacheableVisitorShapes() => new()
    {
        { "PageNumber=1&PageSize=10&" + DefaultSort + "&fromDate={from}&toDate={to}", Caller.Anonymous },
        { "PageNumber=1&PageSize=10&" + DefaultSort + "&searchText=Benchy", Caller.Anonymous },
        { "PageNumber=1&PageSize=10&" + DefaultSort + "&filterByPrinterIds={printer}", Caller.Anonymous },
        { "PageNumber=1&PageSize=10&" + DefaultSort + "&filterByFilamentIds={filament}", Caller.Anonymous },
        { "PageNumber=1&PageSize=250&" + DefaultSort, Caller.Anonymous },
        { "PageNumber=1&PageSize=10&SortColumn=1&SortDirection=1", Caller.Anonymous },
        { "PageNumber=1&PageSize=10", Caller.Anonymous },
        { "PageNumber=1&PageSize=10&" + DefaultSort + "&fromDate={from}&toDate={to}", Caller.OtherUser },
        { "PageNumber=1&PageSize=250&" + DefaultSort, Caller.OtherUser },
    };

    [Theory]
    [MemberData(nameof(NonCacheableVisitorShapes))]
    public async Task NonCacheableVisitorShape_AddsNoCacheEntry_AndReturnsTheRedactedPage(string query, Caller caller)
    {
        var (owner, publicIds, _) = await SeedAsync();
        var other = caller == Caller.OtherUser ? await OtherUserAsync(factory) : null;
        var url = $"/api/Prints/summary?userId={owner.User.Id}&" + query
            .Replace("{from}", Q(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)), StringComparison.Ordinal)
            .Replace("{to}", Q(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero)), StringComparison.Ordinal)
            .Replace("{printer}", owner.Printer.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{filament}", owner.Filament.Id.ToString(), StringComparison.Ordinal);

        for (var pass = 0; pass < 2; pass++)
        {
            var page = await GetJsonAsync(_client, url, caller, owner, other);

            Assert.Equal(publicIds.Order(), Ids(page).Order());
            AssertRedacted(page);
        }

        Assert.Empty(SummaryKeys(owner.User.Id, ViewerKey(caller, owner, other)));
    }

    /// <summary>The public profile's print list: ten a page, newest first, optionally one status.</summary>
    [Theory]
    [InlineData("PageNumber=1&PageSize=10&" + DefaultSort, Caller.Anonymous)]
    [InlineData("PageNumber=1&PageSize=10&" + DefaultSort + "&filterByStatus=3", Caller.Anonymous)]
    [InlineData("PageNumber=1&PageSize=10&" + DefaultSort, Caller.OtherUser)]
    public async Task PublicProfileShape_IsCached(string query, Caller caller)
    {
        var (owner, publicIds, _) = await SeedAsync();
        var other = caller == Caller.OtherUser ? await OtherUserAsync(factory) : null;
        var url = $"/api/Prints/summary?userId={owner.User.Id}&{query}";

        var page = await GetJsonAsync(_client, url, caller, owner, other);

        Assert.Equal(publicIds, Ids(page));
        AssertRedacted(page);
        Assert.Single(SummaryKeys(owner.User.Id, ViewerKey(caller, owner, other)));
    }

    /// <summary>"Load more" on the profile: later pages up to the bound still cache, past it they do not.</summary>
    [Fact]
    public async Task PublicProfileShape_IsCachedUpToThePageBound()
    {
        var (owner, _, _) = await SeedAsync();

        for (var pageNumber = 1; pageNumber <= 6; pageNumber++)
        {
            await GetJsonAsync(_client, $"/api/Prints/summary?userId={owner.User.Id}&PageNumber={pageNumber}&PageSize=10&{DefaultSort}", Caller.Anonymous, owner);
        }

        Assert.Equal(5, SummaryKeys(owner.User.Id, "anon").Count);
    }

    /// <summary>The project page: one hundred a page, filtered to the project, the owner's id.</summary>
    [Fact]
    public async Task ProjectPageShape_IsCached()
    {
        var (owner, publicIds, projectId) = await SeedAsync();
        var url = $"/api/Prints/summary?userId={owner.User.Id}&PageNumber=1&PageSize=100&{DefaultSort}&filterByProjectId={projectId}";

        var page = await GetJsonAsync(_client, url, Caller.Anonymous, owner);

        Assert.Equal(publicIds, Ids(page));
        AssertRedacted(page);
        Assert.Single(SummaryKeys(owner.User.Id, "anon"));
    }

    /// <summary>The owner's own list keeps today's caching, whatever the shape.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OwnerShapes_AreStillCached(bool namesSelf)
    {
        var (owner, _, _) = await SeedAsync();
        var self = namesSelf ? $"userId={owner.User.Id}&" : "";
        var from = Q(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        var to = Q(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));

        var page = await GetJsonAsync(_client,
            $"/api/Prints/summary?{self}PageNumber=7&PageSize=250&SortColumn=1&SortDirection=1&searchText=Benchy&fromDate={from}&toDate={to}",
            Caller.Owner, owner);
        await GetJsonAsync(_client, $"/api/Prints/summary?{self}filterByPrinterIds={owner.Printer.Id}", Caller.Owner, owner);

        Assert.Equal(JsonValueKind.Array, page.GetProperty("items").ValueKind);
        Assert.Equal(2, SummaryKeys(owner.User.Id, owner.User.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)).Count);
    }

    /// <summary>
    /// The owner's full page and a visitor's uncached page never cross: the visitor gets the
    /// redacted shape whichever is computed first.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UncachedVisitorPage_NeverReceivesTheOwnersShape(bool ownerFirst)
    {
        var (owner, _, _) = await SeedAsync();
        var url = $"/api/Prints/summary?userId={owner.User.Id}&PageNumber=1&PageSize=10&{DefaultSort}&searchText=Benchy";

        if (ownerFirst)
        {
            var own = await GetJsonAsync(_client, url, Caller.Owner, owner);
            Assert.Contains(UsageNotesMarker, own.GetRawText(), StringComparison.Ordinal);
        }

        AssertRedacted(await GetJsonAsync(_client, url, Caller.Anonymous, owner));
        Assert.Empty(SummaryKeys(owner.User.Id, "anon"));
    }
}
