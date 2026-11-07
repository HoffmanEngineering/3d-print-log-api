using System.Net;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Project;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// The grouped feed's order is cached on the user's cache version (#309): every request used to
/// read the sort key of every standalone print, every project's start dates and the usage rows
/// of all of them, so a page cost O(the user's prints).
///
/// <para>The first half pins the response the feed returned before the cache, on a fixture whose
/// every sort key is chosen, so each order below is known rather than read back. The second half
/// proves the order is cached and that the writes that change it invalidate it.</para>
///
/// <para>Each test seeds its own user, so none of them depends on another's writes.</para>
/// </summary>
public class GroupedFeedCachingTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Three projects and two standalone prints:
    /// <list type="bullet">
    /// <item>Alpha: pinned to 2026-05-01, one Failed print of 20 g on 2026-01-01.</item>
    /// <item>Bravo: no override, Success prints on 2026-03-02 (5 g) and 2026-03-05 (1 g).</item>
    /// <item>Charlie: no prints, so it sorts on its creation date (now).</item>
    /// <item>Delta: standalone, Success, 2026-04-01, 3 g, notes "needle".</item>
    /// <item>Foxtrot: standalone, Pending, 2026-03-03, no usage.</item>
    /// </list>
    /// </summary>
    private sealed record Fixture(
        string OAuthId, long UserId, long PrinterId, Guid FilamentId,
        Guid Alpha, Guid Bravo, Guid Charlie, long Delta, long Foxtrot);

    private async Task<Fixture> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var oauthId = $"auth0|grouped-cache-{Guid.NewGuid():N}";
        var user = new User { OAuthUserId = oauthId, ViewStatus = User.ProfileViewStatus.Public };
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);

        var printer = new Printer { Name = "Feed Printer", Make = "Prusa", Model = "MK4", UserId = user.Id, IsActive = true };
        db.Printers.Add(printer);

        var now = DateTime.UtcNow;
        var filament = new Filament
        {
            Id = Guid.NewGuid(),
            DisplayName = "Feed PLA",
            MaterialType = "PLA",
            MaterialCategoryNickname = "filament",
            MaterialDensityGramPerCubicCm = 1.24,
            DiameterMm = 1.75,
            InitialNominalWeightMg = 1_000_000,
            Source = Filament.SourceMeasurement.Weight,
            IsActive = true,
            CreatedById = user.Id,
            CreatedDate = now,
            UpdatedById = user.Id,
            UpdatedDate = now,
        };
        db.Filaments.Add(filament);
        await db.SaveChangesAsync(Ct);

        Project NewProject(string name, DateOnly? startOverride = null) => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Status = Project.ProjectStatus.InProgress,
            ViewStatus = Project.ProjectViewStatus.Private,
            StartDateOverride = startOverride,
            CreatedById = user.Id,
            CreatedDate = now,
            UpdatedById = user.Id,
            UpdatedDate = now,
        };

        Print NewPrint(string title, string start, Print.PrintStatus status, Guid? projectId, int? mg, string? notes = null) => new()
        {
            Title = title,
            Notes = notes,
            PrinterId = printer.Id,
            ProjectId = projectId,
            StartDate = DateTimeOffset.Parse(start, System.Globalization.CultureInfo.InvariantCulture),
            PrintTimeInSeconds = 3600,
            Status = status,
            ViewStatus = Print.PrintViewStatus.Private,
            CreatedById = user.Id,
            CreatedDate = now,
            UpdatedById = user.Id,
            UpdatedDate = now,
            FilamentUsage = mg is { } amount
                ? [new PrintFilament { FilamentId = filament.Id, Source = PrintFilament.SourceMeasurement.Weight, AmountMg = amount }]
                : [],
        };

        var alpha = NewProject("Alpha project", new DateOnly(2026, 5, 1));
        var bravo = NewProject("Bravo project");
        var charlie = NewProject("Charlie project");
        db.Projects.AddRange(alpha, bravo, charlie);

        db.Prints.AddRange(
            NewPrint("Alpha part", "2026-01-01T10:00:00Z", Print.PrintStatus.Failed, alpha.Id, 20_000),
            NewPrint("Bravo part one", "2026-03-02T10:00:00Z", Print.PrintStatus.Success, bravo.Id, 5_000),
            NewPrint("Bravo part two", "2026-03-05T10:00:00Z", Print.PrintStatus.Success, bravo.Id, 1_000));
        var delta = NewPrint("Delta print", "2026-04-01T00:00:00Z", Print.PrintStatus.Success, null, 3_000, notes: "needle");
        var foxtrot = NewPrint("Foxtrot print", "2026-03-03T00:00:00Z", Print.PrintStatus.Pending, null, null);
        db.Prints.AddRange(delta, foxtrot);
        await db.SaveChangesAsync(Ct);

        return new Fixture(oauthId, user.Id, printer.Id, filament.Id, alpha.Id, bravo.Id, charlie.Id, delta.Id, foxtrot.Id);
    }

    private async Task<PagedList<GroupedFeedItemDto>> FeedAsync(Fixture f, string query = "")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Prints/grouped?pageNumber=1&pageSize=50{query}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, f.OAuthId);
        var response = await _client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PagedList<GroupedFeedItemDto>>(cancellationToken: Ct))!;
    }

    private static string Key(GroupedFeedItemDto item) =>
        item.Type == "project" ? item.ProjectId!.Value.ToString() : item.Print!.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string[] Keys(PagedList<GroupedFeedItemDto> feed) => feed.Items.Select(Key).ToArray();

    private static string[] Expected(params object[] ids) =>
        ids.Select(id => id is long l ? l.ToString(System.Globalization.CultureInfo.InvariantCulture) : id.ToString()!).ToArray();

    #region The response, pinned

    [Fact]
    public async Task DefaultOrder_IsNewestFirst_AndInterleavesProjectsWithPrints()
    {
        var f = await SeedAsync();

        var feed = await FeedAsync(f);

        Assert.Equal(5, feed.Paging.TotalCount);
        Assert.Equal(Expected(f.Charlie, f.Alpha, f.Delta, f.Foxtrot, f.Bravo), Keys(feed));
    }

    [Fact]
    public async Task DateAscending_IsTheReverse()
    {
        var f = await SeedAsync();

        var feed = await FeedAsync(f, "&sortColumn=StartDate&sortDirection=Asc");

        Assert.Equal(Expected(f.Bravo, f.Foxtrot, f.Delta, f.Alpha, f.Charlie), Keys(feed));
    }

    [Theory]
    [InlineData("Asc")]
    [InlineData("Desc")]
    public async Task TitleOrder_SortsProjectNamesWithPrintTitles(string direction)
    {
        var f = await SeedAsync();

        var feed = await FeedAsync(f, $"&sortColumn=Title&sortDirection={direction}");

        var ascending = Expected(f.Alpha, f.Bravo, f.Charlie, f.Delta, f.Foxtrot);
        Assert.Equal(direction == "Asc" ? ascending : ascending.Reverse().ToArray(), Keys(feed));
    }

    [Fact]
    public async Task FilamentUsageOrder_UsesProjectTotals_AndBreaksTiesPrintsFirst()
    {
        var f = await SeedAsync();

        var descending = await FeedAsync(f, "&sortColumn=FilamentUsage&sortDirection=Desc");
        var ascending = await FeedAsync(f, "&sortColumn=FilamentUsage&sortDirection=Asc");

        // Charlie and Foxtrot both use nothing; the tie-breaker puts the print first either way.
        Assert.Equal(Expected(f.Alpha, f.Bravo, f.Delta, f.Foxtrot, f.Charlie), Keys(descending));
        Assert.Equal(Expected(f.Foxtrot, f.Charlie, f.Delta, f.Bravo, f.Alpha), Keys(ascending));
    }

    [Fact]
    public async Task ProjectRows_CarryTheirSortDateAndTotals()
    {
        var f = await SeedAsync();

        var feed = await FeedAsync(f);
        var alpha = feed.Items.Single(i => i.ProjectId == f.Alpha);
        var bravo = feed.Items.Single(i => i.ProjectId == f.Bravo);
        var charlie = feed.Items.Single(i => i.ProjectId == f.Charlie);

        Assert.Equal(DateTimeOffset.Parse("2026-05-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), alpha.SortDate);
        Assert.Equal(new DateOnly(2026, 5, 1), alpha.ProjectStartDate);
        Assert.Equal(20_000, alpha.TotalFilamentWeightMg);
        Assert.Equal(1, alpha.PrintCount);

        Assert.Equal(DateTimeOffset.Parse("2026-03-02T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture), bravo.SortDate);
        Assert.Equal(new DateOnly(2026, 3, 2), bravo.ProjectStartDate);
        Assert.Equal(6_000, bravo.TotalFilamentWeightMg);
        Assert.Equal(0.0, bravo.TotalFilamentVolumeMl);
        Assert.Equal(2, bravo.PrintCount);
        Assert.Equal(7200, bravo.TotalPrintTimeInSeconds);
        Assert.Equal("Bravo project", bravo.ProjectName);
        Assert.Equal(6_000, Assert.Single(bravo.FilamentUsage!).AmountMg);
        Assert.Equal(f.PrinterId, Assert.Single(bravo.Printers!).Id);

        Assert.Equal(0, charlie.PrintCount);
        Assert.Equal(0, charlie.TotalFilamentWeightMg);

        Assert.All(feed.Items.Where(i => i.Type == "project"), i => Assert.Null(i.FilteredPrintCount));
    }

    [Fact]
    public async Task PrintRows_CarryThePrintAndItsStartAsTheSortDate()
    {
        var f = await SeedAsync();

        var feed = await FeedAsync(f);
        var delta = feed.Items.Single(i => i.Print?.Id == f.Delta);

        Assert.Equal("print", delta.Type);
        Assert.Equal(DateTimeOffset.Parse("2026-04-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), delta.SortDate);
        Assert.Equal("Delta print", delta.Print!.Title);
        Assert.Null(delta.ProjectId);
    }

    [Fact]
    public async Task Pages_TileTheWholeFeed_InOrder()
    {
        var f = await SeedAsync();
        var whole = Keys(await FeedAsync(f, "&sortColumn=Title&sortDirection=Asc"));

        var tiled = new List<string>();
        for (var page = 1; page <= 3; page++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"/api/Prints/grouped?pageNumber={page}&pageSize=2&sortColumn=Title&sortDirection=Asc");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, f.OAuthId);
            var response = await _client.SendAsync(request, Ct);
            var feed = (await response.Content.ReadFromJsonAsync<PagedList<GroupedFeedItemDto>>(cancellationToken: Ct))!;

            Assert.Equal(5, feed.Paging.TotalCount);
            Assert.Equal(page, feed.Paging.CurrentPage);
            tiled.AddRange(Keys(feed));
        }

        Assert.Equal(whole, tiled);
    }

    [Fact]
    public async Task StatusFilter_KeepsEveryProject_CountsItsMatches_AndFiltersStandalonePrints()
    {
        var f = await SeedAsync();

        var feed = await FeedAsync(f, $"&filterByStatus={(int)Print.PrintStatus.Success}");

        // Projects are listed whether or not a print of theirs matched; FilteredPrintCount says
        // how many did. Foxtrot is Pending, so it is the one row filtered out.
        Assert.Equal(4, feed.Paging.TotalCount);
        Assert.Equal(Expected(f.Charlie, f.Alpha, f.Delta, f.Bravo), Keys(feed));
        Assert.Equal(2, feed.Items.Single(i => i.ProjectId == f.Bravo).FilteredPrintCount);
        Assert.Equal(0, feed.Items.Single(i => i.ProjectId == f.Alpha).FilteredPrintCount);
        Assert.Equal(0, feed.Items.Single(i => i.ProjectId == f.Charlie).FilteredPrintCount);
        Assert.Equal(2, feed.Items.Single(i => i.ProjectId == f.Bravo).PrintCount);
    }

    [Fact]
    public async Task Search_MatchesStandaloneNotes_AndCountsProjectMatches()
    {
        var f = await SeedAsync();

        var needle = await FeedAsync(f, "&searchText=needle");
        var bravo = await FeedAsync(f, "&searchText=Bravo");

        Assert.Equal(Expected(f.Charlie, f.Alpha, f.Delta, f.Bravo), Keys(needle));
        Assert.Equal(2, bravo.Items.Single(i => i.ProjectId == f.Bravo).FilteredPrintCount);
        Assert.DoesNotContain(bravo.Items, i => i.Type == "print");
    }

    [Fact]
    public async Task PrinterAndFilamentFilters_SelectStandalonePrints()
    {
        var f = await SeedAsync();

        var byFilament = await FeedAsync(f, $"&filterByFilamentIds={f.FilamentId}");
        var byPrinter = await FeedAsync(f, $"&filterByPrinterIds={f.PrinterId}");

        Assert.Contains(byFilament.Items, i => i.Print?.Id == f.Delta);
        Assert.DoesNotContain(byFilament.Items, i => i.Print?.Id == f.Foxtrot);
        Assert.Equal(1, byFilament.Items.Single(i => i.ProjectId == f.Alpha).FilteredPrintCount);
        Assert.Equal(5, byPrinter.Paging.TotalCount);
    }

    #endregion

    #region Cached on the user's version

    [Fact]
    public async Task TheOrder_IsServedFromTheCache_UntilTheUsersVersionChanges()
    {
        var f = await SeedAsync();
        Assert.Equal(5, (await FeedAsync(f)).Paging.TotalCount);

        // Straight into the database: no write path ran, so nothing bumped the version.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            db.Prints.Add(new Print
            {
                Title = "Behind the cache",
                PrinterId = f.PrinterId,
                StartDate = DateTimeOffset.UtcNow,
                Status = Print.PrintStatus.Success,
                ViewStatus = Print.PrintViewStatus.Private,
                CreatedById = f.UserId,
                UpdatedById = f.UserId,
            });
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(5, (await FeedAsync(f)).Paging.TotalCount);

        factory.Services.GetRequiredService<ICacheVersionService>().InvalidateUserCache(f.UserId);

        Assert.Equal(6, (await FeedAsync(f)).Paging.TotalCount);
    }

    [Fact]
    public async Task OneCachedOrder_ServesEveryPage()
    {
        var f = await SeedAsync();
        await FeedAsync(f, "&sortColumn=Title&sortDirection=Asc");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            db.Prints.Add(new Print
            {
                Title = "AAA behind the cache",
                PrinterId = f.PrinterId,
                Status = Print.PrintStatus.Success,
                ViewStatus = Print.PrintViewStatus.Private,
                CreatedById = f.UserId,
                UpdatedById = f.UserId,
            });
            await db.SaveChangesAsync(Ct);
        }

        // A different page size and page of the same query reads the same cached order.
        var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/Prints/grouped?pageNumber=2&pageSize=3&sortColumn=Title&sortDirection=Asc");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, f.OAuthId);
        var page = (await (await _client.SendAsync(request, Ct)).Content
            .ReadFromJsonAsync<PagedList<GroupedFeedItemDto>>(cancellationToken: Ct))!;

        Assert.Equal(5, page.Paging.TotalCount);
        Assert.Equal(Expected(f.Delta, f.Foxtrot), Keys(page));
    }

    [Fact]
    public async Task CreatingAPrint_ShowsInTheFeed()
    {
        var f = await SeedAsync();
        await FeedAsync(f);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, f.OAuthId);
        request.Content = JsonContent.Create(new AddPrintDTO
        {
            Title = "Golf print",
            PrinterId = f.PrinterId,
            Status = Print.PrintStatus.Success,
            ViewStatus = Print.PrintViewStatus.Private,
            AllowComments = false,
        });
        var created = await _client.SendAsync(request, Ct);
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<PrintDetailDTO>(cancellationToken: Ct))!.Id;

        var feed = await FeedAsync(f);

        Assert.Equal(6, feed.Paging.TotalCount);
        Assert.Contains(feed.Items, i => i.Print?.Id == id);
    }

    [Fact]
    public async Task RenamingAProject_Reorders()
    {
        var f = await SeedAsync();
        await FeedAsync(f, "&sortColumn=Title&sortDirection=Asc");

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/Projects/{f.Charlie}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, f.OAuthId);
        request.Content = JsonContent.Create(new PutProjectDto
        {
            Id = f.Charlie,
            Name = "Aardvark project",
            Status = Project.ProjectStatus.InProgress,
            ViewStatus = Project.ProjectViewStatus.Private,
        });
        (await _client.SendAsync(request, Ct)).EnsureSuccessStatusCode();

        var feed = await FeedAsync(f, "&sortColumn=Title&sortDirection=Asc");

        Assert.Equal(Expected(f.Charlie, f.Alpha, f.Bravo, f.Delta, f.Foxtrot), Keys(feed));
    }

    [Fact]
    public async Task DeletingAPrint_RemovesItFromTheFeed()
    {
        var f = await SeedAsync();
        await FeedAsync(f);

        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/Prints/{f.Delta}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, f.OAuthId);
        (await _client.SendAsync(request, Ct)).EnsureSuccessStatusCode();

        var feed = await FeedAsync(f);

        Assert.Equal(4, feed.Paging.TotalCount);
        Assert.DoesNotContain(feed.Items, i => i.Print?.Id == f.Delta);
    }

    [Fact]
    public async Task MovingAPrintIntoAProject_MovesItsRow()
    {
        var f = await SeedAsync();
        await FeedAsync(f);

        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/Prints/{f.Delta}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, f.OAuthId);
        request.Content = JsonContent.Create(new PatchPrintDto { ProjectId = f.Charlie });
        (await _client.SendAsync(request, Ct)).EnsureSuccessStatusCode();

        var feed = await FeedAsync(f);

        Assert.DoesNotContain(feed.Items, i => i.Print?.Id == f.Delta);
        var charlie = feed.Items.Single(i => i.ProjectId == f.Charlie);
        Assert.Equal(1, charlie.PrintCount);
        Assert.Equal(3_000, charlie.TotalFilamentWeightMg);
    }

    #endregion
}
