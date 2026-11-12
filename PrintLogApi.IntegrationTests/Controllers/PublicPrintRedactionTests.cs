using System.Net;
using System.Text.Json;
using PrintLogApi.Models;
using Xunit;
using static PrintLogApi.IntegrationTests.Controllers.PublicReadSeed;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// A public or unlisted print shows a reader who is not its creator what the public print page
/// renders, and no more: no spool notes, price, storage location, remaining amount, current
/// printer or usage notes on a row with a spool, and no name or id of a project that reader may
/// not open. The creator, through every credential the endpoint accepts, still gets it all.
/// </summary>
public class PublicPrintRedactionTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<User?> OtherFor(Caller caller) =>
        caller == Caller.OtherUser ? await OtherUserAsync(factory) : null;

    private static JsonElement TrackedRow(JsonElement print) =>
        print.GetProperty("filamentUsage").EnumerateArray().Single(r => r.GetProperty("filament").ValueKind != JsonValueKind.Null);

    private static JsonElement UntrackedRow(JsonElement print) =>
        print.GetProperty("filamentUsage").EnumerateArray().Single(r => r.GetProperty("filament").ValueKind == JsonValueKind.Null);

    private static JsonElement SingleItem(JsonElement page) =>
        Assert.Single(page.GetProperty("items").EnumerateArray().ToList());

    /// <summary>What the public print page needs of a spool, and nothing of the owner's inventory.</summary>
    private static void AssertVisitorShape(JsonElement print, Owner owner)
    {
        // The usage rows only: the print's own printer summary (make, model, name) is outside
        // this change, and the seeded printer's name doubles as the loaded-printer marker.
        var raw = print.GetProperty("filamentUsage").GetRawText();
        foreach (var marker in PrivateMarkers)
        {
            Assert.DoesNotContain(marker, raw, StringComparison.Ordinal);
        }

        var row = TrackedRow(print);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("notes").ValueKind);
        Assert.Equal(12_000, row.GetProperty("amountMg").GetInt32());

        var fil = row.GetProperty("filament");
        // Rendered by the page: name, color, swatch, and the unit conversion inputs.
        Assert.Equal(owner.Filament.Id, fil.GetProperty("id").GetGuid());
        Assert.Equal("Galaxy Black PLA", fil.GetProperty("displayName").GetString());
        Assert.Equal("Black", fil.GetProperty("colorName").GetString());
        Assert.Equal("111111", fil.GetProperty("colorHex").GetString());
        Assert.Equal(["111111", "222222"], fil.GetProperty("colors").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal((int)PrintLogApi.Enums.ColorPatternType.Gradient, fil.GetProperty("colorPattern").GetInt32());
        Assert.Equal((int)PrintLogApi.Enums.FilamentFinishType.Matte, fil.GetProperty("finishType").GetInt32());
        Assert.Equal(1.24, fil.GetProperty("materialDensityGramPerCubicCm").GetDouble());
        Assert.Equal(1.75, fil.GetProperty("diameterMm").GetDouble());
        Assert.Equal("PLA", fil.GetProperty("materialType").GetString());
        Assert.Equal("Polymaker", fil.GetProperty("brand").GetString());
        Assert.Equal("filament", fil.GetProperty("materialCategory").GetProperty("nickname").GetString());

        // The owner's spool: none of it.
        foreach (var name in new[]
                 {
                     "notes", "purchasePriceValue", "storageLocation", "filamentRemaining",
                     "filamentLengthRemainingInM", "filamentVolumeRemainingInMl", "loadedInPrinter",
                     "spoolNumber", "initialNominalWeightMg",
                 })
        {
            Assert.True(fil.GetProperty(name).ValueKind == JsonValueKind.Null, $"filament.{name} was {fil.GetProperty(name)}");
        }
        Assert.False(fil.GetProperty("isFavorite").GetBoolean());
        Assert.Equal(default, fil.GetProperty("createdDate").GetDateTime());

        // A row with no spool keeps its notes: the page shows them as the material.
        Assert.Equal(UntrackedRowLabel, UntrackedRow(print).GetProperty("notes").GetString());
    }

    private static void AssertOwnerShape(JsonElement print, Owner owner)
    {
        var row = TrackedRow(print);
        Assert.Equal(UsageNotesMarker, row.GetProperty("notes").GetString());
        var fil = row.GetProperty("filament");
        Assert.Equal(owner.Filament.Id, fil.GetProperty("id").GetGuid());
        Assert.Equal(FilamentNotesMarker, fil.GetProperty("notes").GetString());
        Assert.Equal(PriceMarker, fil.GetProperty("purchasePriceValue").GetString());
        Assert.Equal(StorageMarker, fil.GetProperty("storageLocation").GetString());
        Assert.Equal(7, fil.GetProperty("spoolNumber").GetInt32());
        Assert.Equal(1_000_000, fil.GetProperty("initialNominalWeightMg").GetInt64());
        Assert.True(fil.GetProperty("isFavorite").GetBoolean());
        Assert.NotEqual(default, fil.GetProperty("createdDate").GetDateTime());
        Assert.Equal(UntrackedRowLabel, UntrackedRow(print).GetProperty("notes").GetString());
    }

    // ---- P2: GET /api/Prints/{id} ----

    [Theory]
    [InlineData(PrintViewStatus.Public, Caller.Anonymous)]
    [InlineData(PrintViewStatus.Public, Caller.OtherUser)]
    [InlineData(PrintViewStatus.Unlisted, Caller.Anonymous)]
    [InlineData(PrintViewStatus.Unlisted, Caller.OtherUser)]
    public async Task Detail_ForAReaderWhoIsNotTheCreator_OmitsTheOwnersSpoolData(PrintViewStatus view, Caller caller)
    {
        var owner = await OwnerAsync(factory);
        var print = await PrintAsync(factory, owner, view);

        var body = await GetJsonAsync(_client, $"/api/Prints/{print.Id}", caller, owner, await OtherFor(caller));

        AssertVisitorShape(body, owner);
    }

    [Theory]
    [InlineData(PrintViewStatus.Public, Caller.Owner)]
    [InlineData(PrintViewStatus.Public, Caller.OwnerApiKey)]
    [InlineData(PrintViewStatus.Public, Caller.OwnerDevice)]
    [InlineData(PrintViewStatus.Private, Caller.Owner)]
    [InlineData(PrintViewStatus.Private, Caller.OwnerApiKey)]
    [InlineData(PrintViewStatus.Private, Caller.OwnerDevice)]
    public async Task Detail_ForTheCreator_KeepsTheFullSpoolData(PrintViewStatus view, Caller caller)
    {
        var owner = await OwnerAsync(factory);
        var print = await PrintAsync(factory, owner, view);

        var body = await GetJsonAsync(_client, $"/api/Prints/{print.Id}", caller, owner);

        AssertOwnerShape(body, owner);
        var fil = TrackedRow(body).GetProperty("filament");
        // The detail projection fills these (the summary does not), and the bridge reads its own prints here.
        Assert.Equal(988_000, fil.GetProperty("filamentRemaining").GetInt64());
        Assert.Equal(LoadedPrinterName, fil.GetProperty("loadedInPrinter").GetProperty("name").GetString());
    }

    // ---- P2: GET /api/Prints/summary ----

    [Theory]
    [InlineData(Caller.Anonymous)]
    [InlineData(Caller.OtherUser)]
    public async Task Summary_ForAReaderWhoIsNotTheCreator_OmitsTheOwnersSpoolData(Caller caller)
    {
        var owner = await OwnerAsync(factory);
        await PrintAsync(factory, owner, PrintViewStatus.Public);

        var page = await GetJsonAsync(_client, $"/api/Prints/summary?userId={owner.User.Id}", caller, owner, await OtherFor(caller));

        AssertVisitorShape(SingleItem(page), owner);
    }

    [Theory]
    [InlineData(Caller.Owner, false)]
    [InlineData(Caller.Owner, true)]
    [InlineData(Caller.OwnerApiKey, false)]
    [InlineData(Caller.OwnerApiKey, true)]
    public async Task Summary_ForTheCreator_KeepsTheFullSpoolData(Caller caller, bool namesSelf)
    {
        var owner = await OwnerAsync(factory);
        await PrintAsync(factory, owner, PrintViewStatus.Public);
        var url = namesSelf ? $"/api/Prints/summary?userId={owner.User.Id}" : "/api/Prints/summary";

        var page = await GetJsonAsync(_client, url, caller, owner);

        AssertOwnerShape(SingleItem(page), owner);
    }

    /// <summary>
    /// The page is cached per viewer, and the redaction happens before caching: whichever shape is
    /// computed first, the other viewer still gets their own.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Summary_OwnerAndVisitorShapes_NeverCrossThroughTheCache(bool ownerFirst)
    {
        var owner = await OwnerAsync(factory);
        await PrintAsync(factory, owner, PrintViewStatus.Public);
        // The public profile's request: a visitor is cached only for the public pages' shapes
        // (PrintSummaryCachePolicy), so any other shape would never reach the cache.
        var url = $"/api/Prints/summary?userId={owner.User.Id}&PageNumber=1&PageSize=10&SortColumn=2&SortDirection=2";

        for (var pass = 0; pass < 2; pass++)
        {
            if (ownerFirst)
            {
                AssertOwnerShape(SingleItem(await GetJsonAsync(_client, url, Caller.Owner, owner)), owner);
                AssertVisitorShape(SingleItem(await GetJsonAsync(_client, url, Caller.Anonymous, owner)), owner);
            }
            else
            {
                AssertVisitorShape(SingleItem(await GetJsonAsync(_client, url, Caller.Anonymous, owner)), owner);
                AssertOwnerShape(SingleItem(await GetJsonAsync(_client, url, Caller.Owner, owner)), owner);
            }
        }
    }

    // ---- P5: the project a print belongs to ----

    [Theory]
    [InlineData(Caller.Anonymous)]
    [InlineData(Caller.OtherUser)]
    public async Task Summary_ForAReaderWhoIsNotTheOwner_HidesAPrivateProject(Caller caller)
    {
        var owner = await OwnerAsync(factory);
        var projectId = await ProjectAsync(factory, owner, Project.ProjectViewStatus.Private, "Codename Falcon");
        await PrintAsync(factory, owner, PrintViewStatus.Public, projectId: projectId);

        var response = await GetAsync(_client, $"/api/Prints/summary?userId={owner.User.Id}", caller, owner, await OtherFor(caller));
        var raw = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.DoesNotContain("Codename Falcon", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(projectId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        var item = SingleItem(JsonDocument.Parse(raw).RootElement);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("projectId").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("projectName").ValueKind);
    }

    [Theory]
    [InlineData(Project.ProjectViewStatus.Public, Caller.Anonymous)]
    [InlineData(Project.ProjectViewStatus.Unlisted, Caller.Anonymous)]
    [InlineData(Project.ProjectViewStatus.Public, Caller.OtherUser)]
    [InlineData(Project.ProjectViewStatus.Private, Caller.Owner)]
    [InlineData(Project.ProjectViewStatus.Private, Caller.OwnerApiKey)]
    public async Task Summary_NamesAProjectTheReaderMayOpen(Project.ProjectViewStatus projectView, Caller caller)
    {
        var owner = await OwnerAsync(factory);
        var projectId = await ProjectAsync(factory, owner, projectView, "Shelf brackets");
        await PrintAsync(factory, owner, PrintViewStatus.Public, projectId: projectId);

        var page = await GetJsonAsync(_client, $"/api/Prints/summary?userId={owner.User.Id}", caller, owner, await OtherFor(caller));

        var item = SingleItem(page);
        Assert.Equal(projectId, item.GetProperty("projectId").GetGuid());
        Assert.Equal("Shelf brackets", item.GetProperty("projectName").GetString());
    }

    [Theory]
    [InlineData(Caller.Anonymous)]
    [InlineData(Caller.OtherUser)]
    public async Task Detail_ForAReaderWhoIsNotTheOwner_HidesAPrivateProjectsId(Caller caller)
    {
        var owner = await OwnerAsync(factory);
        var projectId = await ProjectAsync(factory, owner, Project.ProjectViewStatus.Private, "Codename Falcon");
        var print = await PrintAsync(factory, owner, PrintViewStatus.Public, projectId: projectId);

        var body = await GetJsonAsync(_client, $"/api/Prints/{print.Id}", caller, owner, await OtherFor(caller));

        Assert.Equal(JsonValueKind.Null, body.GetProperty("projectId").ValueKind);
    }

    [Theory]
    [InlineData(Project.ProjectViewStatus.Public, Caller.Anonymous)]
    [InlineData(Project.ProjectViewStatus.Unlisted, Caller.OtherUser)]
    [InlineData(Project.ProjectViewStatus.Private, Caller.Owner)]
    [InlineData(Project.ProjectViewStatus.Private, Caller.OwnerApiKey)]
    [InlineData(Project.ProjectViewStatus.Private, Caller.OwnerDevice)]
    public async Task Detail_KeepsTheIdOfAProjectTheReaderMayOpen(Project.ProjectViewStatus projectView, Caller caller)
    {
        var owner = await OwnerAsync(factory);
        var projectId = await ProjectAsync(factory, owner, projectView, "Shelf brackets");
        var print = await PrintAsync(factory, owner, PrintViewStatus.Public, projectId: projectId);

        var body = await GetJsonAsync(_client, $"/api/Prints/{print.Id}", caller, owner, await OtherFor(caller));

        Assert.Equal(projectId, body.GetProperty("projectId").GetGuid());
    }
}
