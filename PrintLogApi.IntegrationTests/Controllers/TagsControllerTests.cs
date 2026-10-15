using System.Net;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Tag;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Print tags (UI #7): user-owned labels, many-to-many with prints. The host is shared across
/// the class, so every test names its tags uniquely and asserts only on what it arranged.
/// </summary>
public class TagsControllerTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static string Unique(string name) => $"{name} {Guid.NewGuid().ToString("N")[..8]}";

    private async Task<HttpResponseMessage> Send(
        HttpMethod method, string url, object? body = null,
        string? oauthId = IntegrationTestSeeder.TestUserOAuthId)
    {
        var request = new HttpRequestMessage(method, url);
        if (oauthId != null)
        {
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, oauthId);
        }
        if (body != null)
        {
            request.Content = JsonContent.Create(body);
        }
        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<TagDto> CreateTag(string name, string? color = null,
        string oauthId = IntegrationTestSeeder.TestUserOAuthId)
    {
        var response = await Send(HttpMethod.Post, "/api/Tags", new SaveTagDto { Name = name, Color = color }, oauthId);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TagDto>(TestContext.Current.CancellationToken))!;
    }

    private async Task<long> CreatePrint(
        long userId, long printerId, int? printTimeSeconds = null, Guid? filamentId = null, int? amountMg = null,
        Print.PrintViewStatus viewStatus = Print.PrintViewStatus.Private)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var print = new Print
        {
            Title = Unique("Tagged print"),
            PrinterId = printerId,
            Status = Print.PrintStatus.Success,
            ViewStatus = viewStatus,
            StartDate = DateTimeOffset.UtcNow.AddDays(-1),
            PrintTimeInSeconds = printTimeSeconds,
            CreatedById = userId,
            UpdatedById = userId,
        };
        if (amountMg.HasValue)
        {
            print.FilamentUsage = [new PrintFilament
            {
                FilamentId = filamentId,
                AmountMg = amountMg,
                Source = PrintFilament.SourceMeasurement.Weight,
                EstimatedSource = PrintFilament.SourceMeasurement.Weight,
            }];
        }
        context.Prints.Add(print);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return print.Id;
    }

    private Task<long> CreateOwnPrint(int? printTimeSeconds = null, Guid? filamentId = null, int? amountMg = null,
        Print.PrintViewStatus viewStatus = Print.PrintViewStatus.Private) =>
        CreatePrint(IntegrationTestSeeder.TestUserId, IntegrationTestSeeder.TestPrinterId,
            printTimeSeconds, filamentId, amountMg, viewStatus);

    private async Task<List<TagDto>> SetPrintTags(long printId, params Guid[] tagIds)
    {
        var response = await Send(HttpMethod.Put, $"/api/Prints/{printId}/tags", new SetPrintTagsDto { TagIds = tagIds });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<TagDto>>(TestContext.Current.CancellationToken))!;
    }

    private async Task<PagedList<PrintSummaryDTO>> Summary(string query, string? oauthId = IntegrationTestSeeder.TestUserOAuthId)
    {
        var response = await Send(HttpMethod.Get, $"/api/Prints/summary?pageNumber=1&pageSize=100&{query}", oauthId: oauthId);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PagedList<PrintSummaryDTO>>(TestContext.Current.CancellationToken))!;
    }

    private async Task<List<TagDto>> ListTags(string oauthId = IntegrationTestSeeder.TestUserOAuthId)
    {
        var response = await Send(HttpMethod.Get, "/api/Tags", oauthId: oauthId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<TagDto>>(TestContext.Current.CancellationToken))!;
    }

    private async Task<long> CreateSecondaryPrinter()
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var printer = new Printer { Name = Unique("Secondary"), UserId = IntegrationTestSeeder.SecondaryUserId, IsActive = true };
        context.Printers.Add(printer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return printer.Id;
    }

    // ---- CRUD ----------------------------------------------------------------------------

    [Fact]
    public async Task Create_TrimsTheName_AndNormalisesTheColour()
    {
        var name = Unique("Gifts");

        var tag = await CreateTag($"  {name}  ", "#AABBCC");

        Assert.NotEqual(Guid.Empty, tag.Id);
        Assert.Equal(name, tag.Name);
        Assert.Equal("#aabbcc", tag.Color);
    }

    [Fact]
    public async Task Create_RejectsADuplicateName_CaseInsensitively()
    {
        var name = Unique("Cosplay");
        await CreateTag(name);

        var response = await Send(HttpMethod.Post, "/api/Tags", new SaveTagDto { Name = name.ToUpperInvariant() });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Create_AllowsTheSameName_ForADifferentUser()
    {
        var name = Unique("Shared name");
        await CreateTag(name);

        var theirs = await CreateTag(name, oauthId: IntegrationTestSeeder.SecondaryUserOAuthId);

        Assert.Equal(name, theirs.Name);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    public async Task Create_RejectsAColourThatIsNotHex(string color)
    {
        var response = await Send(HttpMethod.Post, "/api/Tags", new SaveTagDto { Name = Unique("Colour"), Color = color });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_RejectsABlankName(string name)
    {
        var response = await Send(HttpMethod.Post, "/api/Tags", new SaveTagDto { Name = name });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_ReturnsOnlyTheCallersTags_OrderedByName()
    {
        var mine = await CreateTag(Unique("zz Mine"));
        var theirs = await CreateTag(Unique("Theirs"), oauthId: IntegrationTestSeeder.SecondaryUserOAuthId);

        var tags = await ListTags();

        Assert.Contains(tags, t => t.Id == mine.Id);
        Assert.DoesNotContain(tags, t => t.Id == theirs.Id);
        Assert.Equal(tags.Select(t => t.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase), tags.Select(t => t.Name));
    }

    [Fact]
    public async Task List_RequiresAuthentication()
    {
        var response = await Send(HttpMethod.Get, "/api/Tags", oauthId: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Update_RenamesAndRecolours()
    {
        var tag = await CreateTag(Unique("Before"), "#000000");
        var renamed = Unique("After");

        var response = await Send(HttpMethod.Put, $"/api/Tags/{tag.Id}", new SaveTagDto { Name = renamed, Color = null });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<TagDto>(TestContext.Current.CancellationToken))!;
        Assert.Equal(renamed, updated.Name);
        Assert.Null(updated.Color);
    }

    [Fact]
    public async Task Update_AllowsChangingOnlyTheCaseOfItsOwnName()
    {
        var name = Unique("lowercase");
        var tag = await CreateTag(name);

        var response = await Send(HttpMethod.Put, $"/api/Tags/{tag.Id}", new SaveTagDto { Name = name.ToUpperInvariant() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Update_RejectsANameAnotherOfTheUsersTagsHas()
    {
        var first = await CreateTag(Unique("First"));
        var second = await CreateTag(Unique("Second"));

        var response = await Send(HttpMethod.Put, $"/api/Tags/{second.Id}", new SaveTagDto { Name = first.Name!.ToLowerInvariant() });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Update_AndDelete_AnotherUsersTag_AreNotFound()
    {
        var theirs = await CreateTag(Unique("Not yours"), oauthId: IntegrationTestSeeder.SecondaryUserOAuthId);

        var update = await Send(HttpMethod.Put, $"/api/Tags/{theirs.Id}", new SaveTagDto { Name = "Hijacked" });
        var delete = await Send(HttpMethod.Delete, $"/api/Tags/{theirs.Id}");

        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Contains(await ListTags(IntegrationTestSeeder.SecondaryUserOAuthId), t => t.Id == theirs.Id && t.Name == theirs.Name);
    }

    [Fact]
    public async Task Delete_RemovesTheTag_AndUntagsItsPrints_WithoutDeletingThem()
    {
        var tag = await CreateTag(Unique("Doomed"));
        var printId = await CreateOwnPrint();
        await SetPrintTags(printId, tag.Id);

        var response = await Send(HttpMethod.Delete, $"/api/Tags/{tag.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.DoesNotContain(await ListTags(), t => t.Id == tag.Id);
        var detail = await Send(HttpMethod.Get, $"/api/Prints/{printId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var print = (await detail.Content.ReadFromJsonAsync<PrintDetailDTO>(TestContext.Current.CancellationToken))!;
        Assert.Empty(print.Tags!);
    }

    // ---- Assigning tags to a print -------------------------------------------------------

    [Fact]
    public async Task SetPrintTags_ReplacesTheWholeSet_AndShowsOnDetailAndSummary()
    {
        var a = await CreateTag(Unique("Alpha"));
        var b = await CreateTag(Unique("Beta"));
        var c = await CreateTag(Unique("Gamma"));
        var printId = await CreateOwnPrint();

        await SetPrintTags(printId, a.Id, b.Id);
        var result = await SetPrintTags(printId, b.Id, c.Id, c.Id);

        Assert.Equal(new[] { b.Id, c.Id }.OrderBy(x => x), result.Select(t => t.Id).OrderBy(x => x));

        var detail = (await (await Send(HttpMethod.Get, $"/api/Prints/{printId}"))
            .Content.ReadFromJsonAsync<PrintDetailDTO>(TestContext.Current.CancellationToken))!;
        Assert.Equal(new[] { b.Id, c.Id }.OrderBy(x => x), detail.Tags!.Select(t => t.Id).OrderBy(x => x));

        var summary = await Summary($"filterByTagIds={c.Id}");
        var row = Assert.Single(summary.Items);
        Assert.Equal(printId, row.Id);
        Assert.Equal(new[] { b.Name, c.Name }.Order(), row.Tags!.Select(t => t.Name).Order());
    }

    [Fact]
    public async Task SetPrintTags_WithAnEmptyList_ClearsTheTags()
    {
        var tag = await CreateTag(Unique("Clear me"));
        var printId = await CreateOwnPrint();
        await SetPrintTags(printId, tag.Id);

        var result = await SetPrintTags(printId);

        Assert.Empty(result);
        Assert.Empty((await Summary($"filterByTagIds={tag.Id}")).Items);
    }

    [Fact]
    public async Task SetPrintTags_OnAnotherUsersPrint_IsNotFound()
    {
        var tag = await CreateTag(Unique("Mine"));
        var theirPrint = await CreatePrint(IntegrationTestSeeder.SecondaryUserId, await CreateSecondaryPrinter());

        var response = await Send(HttpMethod.Put, $"/api/Prints/{theirPrint}/tags", new SetPrintTagsDto { TagIds = [tag.Id] });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SetPrintTags_WithAnotherUsersTag_IsRejected_AndChangesNothing()
    {
        var mine = await CreateTag(Unique("Kept"));
        var theirs = await CreateTag(Unique("Foreign"), oauthId: IntegrationTestSeeder.SecondaryUserOAuthId);
        var printId = await CreateOwnPrint();
        await SetPrintTags(printId, mine.Id);

        var response = await Send(HttpMethod.Put, $"/api/Prints/{printId}/tags",
            new SetPrintTagsDto { TagIds = [mine.Id, theirs.Id] });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var row = Assert.Single((await Summary($"filterByTagIds={mine.Id}")).Items);
        Assert.Equal([mine.Id], row.Tags!.Select(t => t.Id));
    }

    [Fact]
    public async Task SetPrintTags_RejectsMoreThanTheLimit()
    {
        var printId = await CreateOwnPrint();
        var ids = Enumerable.Range(0, Tag.MaxTagsPerPrint + 1).Select(_ => Guid.NewGuid()).ToArray();

        var response = await Send(HttpMethod.Put, $"/api/Prints/{printId}/tags", new SetPrintTagsDto { TagIds = ids });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SetPrintTags_AppliesTheLimitAfterRemovingDuplicates()
    {
        var tag = await CreateTag(Unique("Repeated"));
        var printId = await CreateOwnPrint();
        var ids = Enumerable.Repeat(tag.Id, Tag.MaxTagsPerPrint + 5).ToArray();

        var response = await Send(HttpMethod.Put, $"/api/Prints/{printId}/tags", new SetPrintTagsDto { TagIds = ids });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"tagIDs_typo\": []}")]
    [InlineData("{\"tagIds\": null}")]
    public async Task SetPrintTags_WithoutTagIds_IsRejected_AndKeepsTheTags(string body)
    {
        // A missing or misspelled field must not read as "remove every tag".
        var tag = await CreateTag(Unique("Kept"));
        var printId = await CreateOwnPrint();
        await SetPrintTags(printId, tag.Id);

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/Prints/{printId}/tags")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single((await Summary($"filterByTagIds={tag.Id}")).Items);
    }

    [Fact]
    public async Task SetPrintTags_LeavesThePrintItselfAlone()
    {
        // Tags are private, so a tag change must not move the print's UpdatedDate (the public
        // sitemap's lastmod) or rewrite any of its columns.
        var tag = await CreateTag(Unique("Quiet"));
        var printId = await CreateOwnPrint(viewStatus: Print.PrintViewStatus.Public);
        DateTime before;
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            before = await context.Prints.Where(p => p.Id == printId).Select(p => p.UpdatedDate)
                .SingleAsync(TestContext.Current.CancellationToken);
        }

        await SetPrintTags(printId, tag.Id);

        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var after = await context.Prints.Where(p => p.Id == printId).Select(p => p.UpdatedDate)
                .SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(before, after);
        }
    }

    // ---- Summary filter and visibility ---------------------------------------------------

    [Fact]
    public async Task Summary_FilterByTagIds_MatchesAnyOfTheTags()
    {
        var a = await CreateTag(Unique("Any A"));
        var b = await CreateTag(Unique("Any B"));
        var untagged = await CreateOwnPrint();
        var first = await CreateOwnPrint();
        var second = await CreateOwnPrint();
        await SetPrintTags(first, a.Id);
        await SetPrintTags(second, b.Id);

        var result = await Summary($"filterByTagIds={a.Id}&filterByTagIds={b.Id}");

        Assert.Equal(new[] { first, second }.Order(), result.Items.Select(i => i.Id).Order());
        Assert.DoesNotContain(result.Items, i => i.Id == untagged);
    }

    [Fact]
    public async Task Summary_ReflectsATagChange_ThroughTheCache()
    {
        var tag = await CreateTag(Unique("Cached"));
        var printId = await CreateOwnPrint();

        // Prime the cached page, then write: the version bump must make the next read fresh.
        Assert.Empty((await Summary($"filterByTagIds={tag.Id}")).Items);
        await SetPrintTags(printId, tag.Id);
        Assert.Single((await Summary($"filterByTagIds={tag.Id}")).Items);

        await Send(HttpMethod.Put, $"/api/Tags/{tag.Id}", new SaveTagDto { Name = Unique("Renamed") });
        var renamed = Assert.Single((await Summary($"filterByTagIds={tag.Id}")).Items);
        Assert.StartsWith("Renamed", renamed.Tags!.Single().Name);
    }

    [Fact]
    public async Task Tags_AreOwnerOnly_OnPublicSummariesAndPublicPrintDetail()
    {
        var tag = await CreateTag(Unique("Private label"));
        var printId = await CreateOwnPrint(viewStatus: Print.PrintViewStatus.Public);
        await SetPrintTags(printId, tag.Id);

        var publicProfile = await Summary($"userId={IntegrationTestSeeder.TestUserId}", oauthId: IntegrationTestSeeder.SecondaryUserOAuthId);
        var row = Assert.Single(publicProfile.Items, i => i.Id == printId);
        Assert.Null(row.Tags);

        var anonymousDetail = await Send(HttpMethod.Get, $"/api/Prints/{printId}", oauthId: null);
        Assert.Equal(HttpStatusCode.OK, anonymousDetail.StatusCode);
        var detail = (await anonymousDetail.Content.ReadFromJsonAsync<PrintDetailDTO>(TestContext.Current.CancellationToken))!;
        Assert.Null(detail.Tags);
    }

    [Fact]
    public async Task Summary_TagFilterOnSomeoneElsesProfile_MatchesNothing()
    {
        // A visitor cannot use a tag id as an oracle for which of the owner's public prints carry
        // it: the owner's tags are not the visitor's, so the filter matches nothing.
        var tag = await CreateTag(Unique("Oracle"));
        var printId = await CreateOwnPrint(viewStatus: Print.PrintViewStatus.Public);
        await SetPrintTags(printId, tag.Id);

        var result = await Summary($"userId={IntegrationTestSeeder.TestUserId}&filterByTagIds={tag.Id}",
            oauthId: IntegrationTestSeeder.SecondaryUserOAuthId);

        Assert.Empty(result.Items);
    }

    // ---- Stats ---------------------------------------------------------------------------

    [Fact]
    public async Task Stats_AggregateCountTimeFilamentAndCost_PerTag()
    {
        Guid pricedFilamentId;
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var filament = new Filament
            {
                MaterialCategoryNickname = "filament",
                DisplayName = Unique("Priced"),
                PurchasePriceValue = "20",
                InitialNominalWeightMg = 1_000_000,
                CreatedById = IntegrationTestSeeder.TestUserId,
                UpdatedById = IntegrationTestSeeder.TestUserId,
            };
            context.Filaments.Add(filament);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            pricedFilamentId = filament.Id;
        }

        var tag = await CreateTag(Unique("Stats"), "#336699");
        var other = await CreateTag(Unique("Other"));
        var empty = await CreateTag(Unique("Empty"));
        var p1 = await CreateOwnPrint(printTimeSeconds: 3600, filamentId: pricedFilamentId, amountMg: 50_000);
        var p2 = await CreateOwnPrint(printTimeSeconds: 600, filamentId: pricedFilamentId, amountMg: 25_000);
        await SetPrintTags(p1, tag.Id, other.Id);
        await SetPrintTags(p2, tag.Id);

        var response = await Send(HttpMethod.Get, "/api/Tags/stats");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stats = (await response.Content.ReadFromJsonAsync<TagStatsResponse>(TestContext.Current.CancellationToken))!;
        Assert.False(stats.CostUnavailable);

        var row = Assert.Single(stats.Tags!, t => t.Id == tag.Id);
        Assert.Equal(tag.Name, row.Name);
        Assert.Equal("#336699", row.Color);
        Assert.Equal(2, row.PrintCount);
        Assert.Equal(4200, row.TotalPrintTimeInSeconds);
        Assert.Equal(75_000, row.TotalFilamentWeightMg);
        Assert.Equal(1.50m, row.TotalCost);
        Assert.Equal(2, row.CostedPrintCount);

        var otherRow = Assert.Single(stats.Tags!, t => t.Id == other.Id);
        Assert.Equal(1, otherRow.PrintCount);
        Assert.Equal(1.00m, otherRow.TotalCost);

        // A tag with no prints is still listed, with zeroes and no cost rather than a fake $0.00.
        var emptyRow = Assert.Single(stats.Tags!, t => t.Id == empty.Id);
        Assert.Equal(0, emptyRow.PrintCount);
        Assert.Null(emptyRow.TotalCost);
    }

    [Fact]
    public async Task Stats_DoNotIncludeAnotherUsersTags()
    {
        var theirs = await CreateTag(Unique("Their stats"), oauthId: IntegrationTestSeeder.SecondaryUserOAuthId);

        var stats = (await (await Send(HttpMethod.Get, "/api/Tags/stats"))
            .Content.ReadFromJsonAsync<TagStatsResponse>(TestContext.Current.CancellationToken))!;

        Assert.DoesNotContain(stats.Tags!, t => t.Id == theirs.Id);
    }

    // ---- Lifecycle -----------------------------------------------------------------------

    [Fact]
    public async Task DeletingAPrint_RemovesItsTagLinks_AndKeepsTheTag()
    {
        var tag = await CreateTag(Unique("Survivor"));
        var printId = await CreateOwnPrint();
        await SetPrintTags(printId, tag.Id);

        var response = await Send(HttpMethod.Delete, $"/api/Prints/{printId}");
        Assert.True(response.IsSuccessStatusCode, $"Delete print returned {response.StatusCode}");

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        Assert.False(await context.PrintTags.AnyAsync(pt => pt.PrintId == printId, TestContext.Current.CancellationToken));
        Assert.Contains(await ListTags(), t => t.Id == tag.Id);
    }
}
