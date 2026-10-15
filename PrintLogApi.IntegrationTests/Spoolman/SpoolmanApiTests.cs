using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Services;
using PrintLogApi.Services.Spoolman;
using Xunit;

namespace PrintLogApi.IntegrationTests.Spoolman;

/// <summary>
/// The read-only Spoolman v1 API (#177), driven the way its real clients drive it. The request
/// sequences are the ones each client makes (Moonraker's from <c>components/spoolman.py</c>; the
/// UIs' through Moonraker's <c>/server/spoolman/proxy</c>, which forwards path and query as they
/// are, credential included), and bodies are checked against Spoolman v0.27.0's models
/// (<c>spoolman/api/v1/models.py</c>): the keys each model may carry, the keys it must, and no
/// nulls, since Spoolman serializes with <c>exclude_none</c>.
/// </summary>
public class SpoolmanApiTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private const string Base = "/spoolman/api/v1";

    private static readonly string[] SpoolKeys =
    [
        "id", "registered", "first_used", "last_used", "filament", "price", "remaining_weight", "initial_weight",
        "spool_weight", "used_weight", "remaining_length", "used_length", "location", "lot_nr", "comment", "archived",
        "extra", "tags",
    ];
    private static readonly string[] SpoolRequired = ["id", "registered", "filament", "used_weight", "used_length", "archived", "extra", "tags"];

    private static readonly string[] FilamentKeys =
    [
        "id", "registered", "name", "vendor", "material", "price", "density", "diameter", "weight", "spool_weight",
        "article_number", "comment", "settings_extruder_temp", "settings_bed_temp", "color_hex", "multi_color_hexes",
        "multi_color_direction", "external_id", "extra", "tags",
    ];
    private static readonly string[] FilamentRequired = ["id", "registered", "density", "diameter", "extra", "tags"];

    private static readonly string[] VendorKeys = ["id", "registered", "name", "comment", "empty_spool_weight", "external_id", "extra"];
    private static readonly string[] VendorRequired = ["id", "registered", "name", "extra"];

    private static readonly string[] InfoKeys =
    [
        "version", "debug_mode", "automatic_backups", "data_dir", "logs_dir", "backups_dir", "db_type", "external_db_name",
        "git_commit", "build_date",
    ];
    private static readonly string[] InfoRequired =
        ["version", "debug_mode", "automatic_backups", "data_dir", "logs_dir", "backups_dir", "db_type", "external_db_name"];

    private readonly HttpClient _client = factory.CreateClient();
    private readonly SpoolmanTestSupport _support = new(factory.Services);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #region Client sequences

    /// <summary>
    /// Moonraker: on connect (and whenever the active spool is set) <c>GET /v1/spool/{id}</c>, then
    /// a <c>PUT /v1/spool/{id}/use</c> with <c>{"use_length": mm}</c> every sync interval.
    /// </summary>
    [Fact]
    public async Task Moonraker_ConnectCheckThenUsageReports()
    {
        var (user, secret) = await ConnectedUserAsync();
        var spool = await AddSpoolAsync(user, "Galaxy Black", brand: "Prusament");

        var check = await GetAsync(secret, $"/spool/{spool.SpoolNumber}");
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        var before = await JsonAsync(check);
        AssertModel(before, SpoolKeys, SpoolRequired);

        for (var i = 0; i < 3; i++)
        {
            var use = await SendAsync(secret, HttpMethod.Put, $"/spool/{spool.SpoolNumber}/use", """{"use_length": 152.37}""");
            Assert.Equal(HttpStatusCode.OK, use.StatusCode);
            var returned = await JsonAsync(use);
            AssertModel(returned, SpoolKeys, SpoolRequired);
            Assert.Equal(spool.SpoolNumber, returned.GetProperty("id").GetInt32());
        }

        // Accepted, not persisted (#171): the spool reads exactly as it did.
        var after = await JsonAsync(await GetAsync(secret, $"/spool/{spool.SpoolNumber}"));
        Assert.Equal(before.GetRawText(), after.GetRawText());
    }

    [Fact]
    public async Task Moonraker_AnUnknownSpool_Is404_WhichClearsItsActiveSpool()
    {
        var (_, secret) = await ConnectedUserAsync();

        var check = await GetAsync(secret, "/spool/999");
        var use = await SendAsync(secret, HttpMethod.Put, "/spool/999/use", """{"use_length": 1}""");

        Assert.Equal(HttpStatusCode.NotFound, check.StatusCode);
        Assert.Equal("No spool with ID 999 found.", (await JsonAsync(check)).GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.NotFound, use.StatusCode);
        await SpoolmanTestSupport.AssertSpoolmanErrorAsync(use);
    }

    /// <summary>Mainsail: <c>info</c>, <c>health</c>, <c>vendor</c>, <c>spool</c>.</summary>
    [Fact]
    public async Task Mainsail_InfoHealthVendorSpool()
    {
        var (user, secret) = await ConnectedUserAsync();
        await AddSpoolAsync(user, "Galaxy Black", brand: "Prusament");
        await AddSpoolAsync(user, "Jet Black", brand: "Polymaker");

        var info = await JsonAsync(await GetAsync(secret, "/info"));
        AssertModel(info, InfoKeys, InfoRequired);
        Assert.Equal("0.27.0", info.GetProperty("version").GetString());

        Assert.Equal("""{"status":"healthy"}""", await (await GetAsync(secret, "/health")).Content.ReadAsStringAsync(Ct));

        var vendors = await GetAsync(secret, "/vendor");
        Assert.Equal("2", vendors.Headers.GetValues("x-total-count").Single());
        var vendorList = await JsonAsync(vendors);
        Assert.All(vendorList.EnumerateArray(), v => AssertModel(v, VendorKeys, VendorRequired));
        Assert.Equal(["Polymaker", "Prusament"], vendorList.EnumerateArray().Select(v => v.GetProperty("name").GetString()).ToArray());

        var spools = await GetAsync(secret, "/spool");
        Assert.Equal("2", spools.Headers.GetValues("x-total-count").Single());
        Assert.All((await JsonAsync(spools)).EnumerateArray(), s => AssertModel(s, SpoolKeys, SpoolRequired));
    }

    /// <summary>Fluidd: <c>spool</c>, <c>info</c>, <c>setting/currency</c>.</summary>
    [Fact]
    public async Task Fluidd_SpoolInfoCurrency()
    {
        var (user, secret) = await ConnectedUserAsync();
        await AddSpoolAsync(user, "Galaxy Black", brand: "Prusament");

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(secret, "/spool")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(secret, "/info")).StatusCode);

        var currency = await GetAsync(secret, "/setting/currency");
        Assert.Equal(HttpStatusCode.OK, currency.StatusCode);
        // Spoolman's value is itself JSON: the string "USD" with its quotes.
        Assert.Equal("""{"value":"\"USD\"","is_set":false,"type":"string"}""", await currency.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Fluidd_Currency_IsTheUsersOwnWhenSet()
    {
        var (user, secret) = await ConnectedUserAsync();
        await WithDbAsync(async db =>
        {
            db.UserSettings.Add(new UserSetting { UserId = user.Id, UserSettingTypeId = 5, Value = "eur", CreatedById = user.Id, UpdatedById = user.Id });
            await db.SaveChangesAsync(Ct);
            return 0;
        });

        var currency = await JsonAsync(await GetAsync(secret, "/setting/currency"));

        Assert.Equal("\"EUR\"", currency.GetProperty("value").GetString());
        Assert.True(currency.GetProperty("is_set").GetBoolean());
    }

    /// <summary>KlipperScreen: lists the spools, then reads the one the user taps.</summary>
    [Fact]
    public async Task KlipperScreen_ListThenSelect()
    {
        var (user, secret) = await ConnectedUserAsync();
        await AddSpoolAsync(user, "First");
        var second = await AddSpoolAsync(user, "Second");

        var list = await JsonAsync(await GetAsync(secret, "/spool"));
        var tapped = list.EnumerateArray().Single(s => s.GetProperty("filament").GetProperty("name").GetString() == "Second");
        var selected = await GetAsync(secret, $"/spool/{tapped.GetProperty("id").GetInt32()}");

        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        Assert.Equal(second.SpoolNumber, (await JsonAsync(selected)).GetProperty("id").GetInt32());
    }

    #endregion

    #region Mapping

    [Fact]
    public async Task ASpool_MapsEveryFieldSpoolmanReads()
    {
        var (user, secret) = await ConnectedUserAsync();
        var spool = await AddSpoolAsync(user, "Galaxy Black", brand: "Prusament", dto =>
        {
            dto.MaterialType = "PETG";
            dto.ColorHex = "1a2b3c";
            dto.InitialNominalWeightMg = 1_000_000;
            dto.SpoolWeightMg = 250_000;
            dto.DiameterMm = 1.75;
            dto.MaterialDensityGramPerCubicCm = 1.27;
            dto.RecommendedTemp = 240.4;
            dto.RecommendedBedTemp = 85;
            dto.StorageLocation = "Dry box 2";
            dto.Notes = "Grün label, opened";
            dto.PurchasePriceValue = "24.99";
        });
        await AddUsageAsync(user, spool.Id, amountMg: 200_000, at: new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero));

        var response = await GetAsync(secret, $"/spool/{spool.SpoolNumber}");
        var raw = await response.Content.ReadAsStringAsync(Ct);
        var json = JsonDocument.Parse(raw).RootElement;
        var filament = json.GetProperty("filament");

        Assert.Equal(spool.SpoolNumber, json.GetProperty("id").GetInt32());
        Assert.Equal(spool.SpoolNumber, filament.GetProperty("id").GetInt32());
        Assert.Equal(800, json.GetProperty("remaining_weight").GetDouble(), 3);
        Assert.Equal(200, json.GetProperty("used_weight").GetDouble(), 3);
        Assert.Equal(1000, json.GetProperty("initial_weight").GetDouble(), 3);
        Assert.Equal(250, json.GetProperty("spool_weight").GetDouble(), 3);
        // 800 g of 1.75 mm PETG at 1.27 g/cm³ is about 262 m.
        Assert.Equal(SpoolmanInventoryService.LengthMm(800, 1.27, 1.75), json.GetProperty("remaining_length").GetDouble(), 3);
        Assert.InRange(json.GetProperty("remaining_length").GetDouble(), 261_000, 263_000);
        Assert.Equal(SpoolmanInventoryService.LengthMm(200, 1.27, 1.75), json.GetProperty("used_length").GetDouble(), 3);
        Assert.Equal(24.99, json.GetProperty("price").GetDouble(), 3);
        Assert.Equal("Dry box 2", json.GetProperty("location").GetString());
        Assert.Equal("Grün label, opened", json.GetProperty("comment").GetString());
        Assert.Contains("Grün", raw); // not \u00FC: FastAPI writes UTF-8 as is
        Assert.False(json.GetProperty("archived").GetBoolean());
        Assert.Equal("2026-03-04T05:06:07Z", json.GetProperty("last_used").GetString());
        Assert.Equal("2026-03-04T05:06:07Z", json.GetProperty("first_used").GetString());
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d{6})?Z$", json.GetProperty("registered").GetString());

        Assert.Equal("Galaxy Black", filament.GetProperty("name").GetString());
        Assert.Equal("PETG", filament.GetProperty("material").GetString());
        Assert.Equal(1.27, filament.GetProperty("density").GetDouble(), 3);
        Assert.Equal(1.75, filament.GetProperty("diameter").GetDouble(), 3);
        Assert.Equal(1000, filament.GetProperty("weight").GetDouble(), 3);
        Assert.Equal(240, filament.GetProperty("settings_extruder_temp").GetInt32());
        Assert.Equal(85, filament.GetProperty("settings_bed_temp").GetInt32());
        Assert.Equal("1A2B3C", filament.GetProperty("color_hex").GetString());
        Assert.False(filament.TryGetProperty("multi_color_hexes", out _));
        Assert.Equal("Prusament", filament.GetProperty("vendor").GetProperty("name").GetString());
        Assert.Equal(SpoolmanInventoryService.VendorId("Prusament"), filament.GetProperty("vendor").GetProperty("id").GetInt32());

        // Required Spoolman members present as empty, never null.
        Assert.Equal(JsonValueKind.Object, json.GetProperty("extra").ValueKind);
        Assert.Empty(json.GetProperty("tags").EnumerateArray());
        Assert.DoesNotContain("null", raw);
    }

    [Fact]
    public async Task SeveralColours_AreMultiColorHexes_AndNeverAlsoColorHex()
    {
        var (user, secret) = await ConnectedUserAsync();
        var spool = await AddSpoolAsync(user, "Rainbow", configure: dto => dto.Colors = ["ff0000", "00FF00", "#0000ff"]);

        var filament = (await JsonAsync(await GetAsync(secret, $"/spool/{spool.SpoolNumber}"))).GetProperty("filament");

        Assert.Equal("FF0000,00FF00,0000FF", filament.GetProperty("multi_color_hexes").GetString());
        Assert.Equal("coaxial", filament.GetProperty("multi_color_direction").GetString());
        Assert.False(filament.TryGetProperty("color_hex", out _));
    }

    [Fact]
    public async Task AnUnparseablePrice_AndMissingValues_AreOmitted_NotNull()
    {
        var (user, secret) = await ConnectedUserAsync();
        var spool = await AddSpoolAsync(user, "Bare", configure: dto =>
        {
            dto.Brand = null;
            dto.PurchasePriceValue = "about twenty";
            dto.StorageLocation = null;
            dto.Notes = null;
            dto.InitialNominalWeightMg = null;
            dto.SpoolWeightMg = null;
            dto.RecommendedTemp = null;
        });

        var json = await JsonAsync(await GetAsync(secret, $"/spool/{spool.SpoolNumber}"));
        var filament = json.GetProperty("filament");

        foreach (var absent in new[] { "price", "location", "comment", "remaining_weight", "remaining_length", "initial_weight", "spool_weight", "first_used", "last_used", "lot_nr" })
        {
            Assert.False(json.TryGetProperty(absent, out _), absent);
        }
        foreach (var absent in new[] { "vendor", "weight", "settings_extruder_temp" })
        {
            Assert.False(filament.TryGetProperty(absent, out _), absent);
        }
        AssertModel(json, SpoolKeys, SpoolRequired);
    }

    [Fact]
    public async Task VendorIds_AreStable_EvenWhenTheSpoolThatIntroducedTheBrandIsDeleted()
    {
        var (user, secret) = await ConnectedUserAsync();
        var first = await AddSpoolAsync(user, "Old roll", brand: "Sunlu");
        await AddSpoolAsync(user, "New roll", brand: "sunlu ");

        var before = await JsonAsync(await GetAsync(secret, "/vendor"));
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IFilamentService>().DeleteFilament(first.Id);
        }
        var after = await JsonAsync(await GetAsync(secret, "/vendor"));

        var id = Assert.Single(before.EnumerateArray()).GetProperty("id").GetInt32();
        Assert.Equal(id, Assert.Single(after.EnumerateArray()).GetProperty("id").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(secret, $"/vendor/{id}")).StatusCode);
    }

    [Fact]
    public async Task OnlyTheFilamentCategory_IsASpool()
    {
        var (user, secret) = await ConnectedUserAsync();
        await AddSpoolAsync(user, "Filament");
        var resin = await AddSpoolAsync(user, "Resin bottle", configure: dto => dto.MaterialCategoryNickname = "resin");

        var spools = await JsonAsync(await GetAsync(secret, "/spool?allow_archived=true"));

        Assert.Equal(["Filament"], spools.EnumerateArray().Select(s => s.GetProperty("filament").GetProperty("name").GetString()).ToArray());
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(secret, $"/spool/{resin.SpoolNumber}")).StatusCode);
    }

    #endregion

    #region Scoping, archived, filters

    [Fact]
    public async Task AnotherUsersSpool_Is404_AndNeverListed()
    {
        var (_, secret) = await ConnectedUserAsync();
        var other = await _support.CreateUserAsync();
        await AddSpoolAsync(other, "Theirs 1");
        await AddSpoolAsync(other, "Theirs 2");
        var theirs = await AddSpoolAsync(other, "Theirs 3");

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(secret, $"/spool/{theirs.SpoolNumber}")).StatusCode);
        Assert.Empty((await JsonAsync(await GetAsync(secret, "/spool?allow_archived=true"))).EnumerateArray());
        Assert.Empty((await JsonAsync(await GetAsync(secret, "/filament"))).EnumerateArray());
    }

    [Fact]
    public async Task ArchivedSpools_AreHidden_UnlessAllowed()
    {
        var (user, secret) = await ConnectedUserAsync();
        await AddSpoolAsync(user, "Active");
        var archived = await AddSpoolAsync(user, "Finished", configure: dto => dto.IsActive = false);

        var hidden = await GetAsync(secret, "/spool");
        var shown = await GetAsync(secret, "/spool?allow_archived=true");

        Assert.Equal("1", hidden.Headers.GetValues("x-total-count").Single());
        Assert.Equal(["Active"], Names(await JsonAsync(hidden)));
        Assert.Equal("2", shown.Headers.GetValues("x-total-count").Single());
        Assert.True((await JsonAsync(shown)).EnumerateArray().Single(s => s.GetProperty("id").GetInt32() == archived.SpoolNumber).GetProperty("archived").GetBoolean());
        // Asked for by id, an archived spool is still a spool: a 404 would clear Moonraker's active spool.
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(secret, $"/spool/{archived.SpoolNumber}")).StatusCode);
    }

    [Fact]
    public async Task CommonFilters_SortAndPaging()
    {
        var (user, secret) = await ConnectedUserAsync();
        await AddSpoolAsync(user, "A", brand: "Prusament", dto => { dto.MaterialType = "PLA"; dto.StorageLocation = "Shelf 1"; });
        await AddSpoolAsync(user, "B", brand: "Polymaker", dto => { dto.MaterialType = "PETG"; dto.StorageLocation = "Shelf 2"; });
        await AddSpoolAsync(user, "C", brand: "Prusament", dto => { dto.MaterialType = "PLA-CF"; dto.StorageLocation = null; });

        Assert.Equal(["A", "C"], Names(await JsonAsync(await GetAsync(secret, "/spool?filament.material=pla"))));
        Assert.Equal(["A"], Names(await JsonAsync(await GetAsync(secret, "/spool?filament.material=%22PLA%22"))));
        Assert.Equal(["A", "C"], Names(await JsonAsync(await GetAsync(secret, "/spool?filament.vendor.name=prusa"))));
        Assert.Equal(["B"], Names(await JsonAsync(await GetAsync(secret, "/spool?location=shelf%202"))));
        Assert.Equal(["C"], Names(await JsonAsync(await GetAsync(secret, "/spool?location="))));
        Assert.Equal(["A", "B"], Names(await JsonAsync(await GetAsync(secret, "/spool?location=Shelf%201,Shelf%202"))));
        // Deprecated spellings still filter.
        Assert.Equal(["B"], Names(await JsonAsync(await GetAsync(secret, "/spool?vendor_name=Polymaker"))));

        Assert.Equal(["C", "B", "A"], Names(await JsonAsync(await GetAsync(secret, "/spool?sort=filament.name:desc"))));
        // Nulls last whichever way: C has no location.
        Assert.Equal(["B", "A", "C"], Names(await JsonAsync(await GetAsync(secret, "/spool?sort=location:desc"))));
        Assert.Equal(["A", "B", "C"], Names(await JsonAsync(await GetAsync(secret, "/spool?sort=location:asc"))));

        var page = await GetAsync(secret, "/spool?sort=filament.name:asc&limit=1&offset=1");
        Assert.Equal("3", page.Headers.GetValues("x-total-count").Single());
        Assert.Equal(["B"], Names(await JsonAsync(page)));
    }

    [Fact]
    public async Task ABadSort_Is400WithAMessage()
    {
        var (_, secret) = await ConnectedUserAsync();

        var response = await GetAsync(secret, "/spool?sort=location");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await SpoolmanTestSupport.AssertSpoolmanErrorAsync(response);
    }

    [Fact]
    public async Task EditingASpoolInTheWebApp_ShowsUpAtOnce()
    {
        var (user, secret) = await ConnectedUserAsync();
        var spool = await AddSpoolAsync(user, "Before");
        Assert.Equal(["Before"], Names(await JsonAsync(await GetAsync(secret, "/spool")))); // now cached

        var detail = await RestAsync<FilamentDetailDto>(user, HttpMethod.Get, $"/api/Filaments/{spool.Id}");
        detail.DisplayName = "After";
        var put = await RestRawAsync(user, HttpMethod.Put, $"/api/Filaments/{spool.Id}", detail);
        Assert.True(put.IsSuccessStatusCode, $"{(int)put.StatusCode}");

        Assert.Equal(["After"], Names(await JsonAsync(await GetAsync(secret, "/spool"))));
    }

    [Fact]
    public async Task AccountDeletion_RefusesTheOldSecretAtOnce()
    {
        var (user, secret) = await ConnectedUserAsync();
        await AddSpoolAsync(user, "Galaxy Black");
        // Served once, so the credential is cached as valid.
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(secret, "/spool")).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var tracked = await db.Users.SingleAsync(u => u.Id == user.Id, Ct);
            await scope.ServiceProvider.GetRequiredService<IUserDeletionService>().DeleteAllDataForUser(tracked);
        }

        var response = await GetAsync(secret, "/spool");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A spool the old app created during the #174 deploy has no number. Numbering runs on a
    /// cache miss only (#195), not on every request: a cached read leaves the straggler alone, and
    /// the next read after a version bump numbers it, lists it, and lets <c>/use</c> resolve it.
    /// </summary>
    [Fact]
    public async Task AnUnnumberedSpool_IsNumberedOnTheNextCacheMiss_NotOnEveryRead()
    {
        var (user, secret) = await ConnectedUserAsync();
        await AddSpoolAsync(user, "Numbered");
        Assert.Equal(["Numbered"], Names(await JsonAsync(await GetAsync(secret, "/spool")))); // now cached

        var straggler = await WithDbAsync(async db =>
        {
            var filament = new Filament
            {
                DisplayName = "Created by the old app",
                MaterialCategoryNickname = "filament",
                MaterialDensityGramPerCubicCm = 1.24,
                DiameterMm = 1.75,
                IsActive = true,
                CreatedById = user.Id,
                UpdatedById = user.Id,
            };
            db.Filaments.Add(filament);
            await db.SaveChangesAsync(Ct);
            return filament.Id;
        });
        Task<int?> StrayNumberAsync() => WithDbAsync(db =>
            db.Filaments.Where(f => f.Id == straggler).Select(f => f.SpoolNumber).SingleAsync(Ct));

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(secret, HttpMethod.Put, "/spool/2/use", """{"use_length": 1}""")).StatusCode);
        Assert.Null(await StrayNumberAsync());

        // The old app's write bumps the old app's in-process version, not this one's; a write
        // here or a restart is what makes this cache miss.
        factory.Services.GetRequiredService<ICacheVersionService>().InvalidateUserCache(user.Id);

        Assert.Equal(["Numbered", "Created by the old app"], Names(await JsonAsync(await GetAsync(secret, "/spool"))));
        Assert.Equal(2, await StrayNumberAsync());
        var use = await SendAsync(secret, HttpMethod.Put, "/spool/2/use", """{"use_length": 1}""");
        Assert.Equal(HttpStatusCode.OK, use.StatusCode);
        Assert.Equal(2, (await JsonAsync(use)).GetProperty("id").GetInt32());
    }

    #endregion

    #region The rest of the read surface

    [Fact]
    public async Task Filaments_OnePerSpool()
    {
        var (user, secret) = await ConnectedUserAsync();
        var spool = await AddSpoolAsync(user, "Galaxy Black", brand: "Prusament");

        var list = await GetAsync(secret, "/filament");
        var one = await GetAsync(secret, $"/filament/{spool.SpoolNumber}");

        Assert.Equal("1", list.Headers.GetValues("x-total-count").Single());
        AssertModel(Assert.Single((await JsonAsync(list)).EnumerateArray()), FilamentKeys, FilamentRequired);
        AssertModel(await JsonAsync(one), FilamentKeys, FilamentRequired);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(secret, "/filament/999")).StatusCode);
    }

    [Fact]
    public async Task Lists_LocationsMaterialsLotAndArticleNumbers()
    {
        var (user, secret) = await ConnectedUserAsync();
        await AddSpoolAsync(user, "A", configure: dto => { dto.MaterialType = "PLA"; dto.StorageLocation = "Shelf 1"; });
        await AddSpoolAsync(user, "B", configure: dto => { dto.MaterialType = "PETG"; dto.StorageLocation = "Shelf 1"; });

        Assert.Equal("""["Shelf 1"]""", await (await GetAsync(secret, "/location")).Content.ReadAsStringAsync(Ct));
        Assert.Equal("""["PETG","PLA"]""", await (await GetAsync(secret, "/material")).Content.ReadAsStringAsync(Ct));
        Assert.Equal("[]", await (await GetAsync(secret, "/lot-number")).Content.ReadAsStringAsync(Ct));
        Assert.Equal("[]", await (await GetAsync(secret, "/article-number")).Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task FieldsAndSettings()
    {
        var (_, secret) = await ConnectedUserAsync();

        Assert.Equal("[]", await (await GetAsync(secret, "/field/spool")).Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(secret, "/field/printer")).StatusCode);

        var settings = await JsonAsync(await GetAsync(secret, "/setting/"));
        Assert.Equal("\"USD\"", settings.GetProperty("currency").GetProperty("value").GetString());
        Assert.Equal("[]", settings.GetProperty("extra_fields_spool").GetProperty("value").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(secret, "/setting/no_such_setting")).StatusCode);
    }

    [Theory]
    [InlineData("POST", "/spool")]
    [InlineData("PATCH", "/spool/1")]
    [InlineData("DELETE", "/spool/1")]
    [InlineData("PUT", "/spool/1/measure")]
    [InlineData("POST", "/filament")]
    [InlineData("PATCH", "/filament/1")]
    [InlineData("POST", "/vendor")]
    [InlineData("DELETE", "/vendor/1")]
    [InlineData("POST", "/setting/currency")]
    [InlineData("POST", "/field/spool/color")]
    public async Task Writes_Are400_NotSupportedYet(string method, string path)
    {
        var (_, secret) = await ConnectedUserAsync();

        var response = await SendAsync(secret, new HttpMethod(method), path, "{}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("""{"message":"Not supported yet by 3D Print Log"}""", await response.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("""{"use_length": 1, "use_weight": 1}""", "Only specify either use_weight or use_length.")]
    [InlineData("""{}""", "Either use_weight or use_length must be specified.")]
    [InlineData("""not json""", "The request body is not valid JSON.")]
    public async Task Use_ValidatesItsBody_AsSpoolmanDoes(string body, string message)
    {
        var (user, secret) = await ConnectedUserAsync();
        var spool = await AddSpoolAsync(user, "Galaxy Black");

        var response = await SendAsync(secret, HttpMethod.Put, $"/spool/{spool.SpoolNumber}/use", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(message, (await JsonAsync(response)).GetProperty("message").GetString());
    }

    [Fact]
    public async Task Use_ByWeight_IsCountedInMillimetres()
    {
        var (user, secret) = await ConnectedUserAsync();
        var spool = await AddSpoolAsync(user, "Galaxy Black");
        var before = SpoolmanMetrics.UseMillimetres.Value;
        var calls = SpoolmanMetrics.UseCalls.WithLabels("weight").Value;

        var response = await SendAsync(secret, HttpMethod.Put, $"/spool/{spool.SpoolNumber}/use", """{"use_weight": 3}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(SpoolmanMetrics.UseCalls.WithLabels("weight").Value >= calls + 1);
        Assert.True(SpoolmanMetrics.UseMillimetres.Value - before >= SpoolmanInventoryService.LengthMm(3, 1.24, 1.75) - 0.001);
    }

    [Fact]
    public async Task TheSpoolmanRoutes_AreNotInThePublicOpenApiDocument()
    {
        var document = await _client.GetStringAsync("/swagger/v1/swagger.json", Ct);

        // The connection-management endpoints (#175) are ours and belong there; the Spoolman
        // protocol under /spoolman/api is not.
        Assert.DoesNotContain("\"/spoolman/", document);
    }

    #endregion

    #region Helpers

    private async Task<(User User, string Secret)> ConnectedUserAsync()
    {
        var user = await _support.CreateUserAsync();
        var connection = await _support.ConnectAsync(user);
        return (user, connection.Secret!);
    }

    private async Task<Filament> AddSpoolAsync(User user, string name, string? brand = "Generic", Action<AddFilamentDto>? configure = null)
    {
        var dto = new AddFilamentDto
        {
            DisplayName = name,
            Brand = brand,
            MaterialType = "PLA",
            MaterialCategoryNickname = "filament",
            ColorHex = "112233",
            DiameterMm = 1.75,
            InitialNominalWeightMg = 1_000_000,
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true,
        };
        configure?.Invoke(dto);
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IFilamentService>().AddFilament(dto, user.Id);
    }

    private async Task AddUsageAsync(User user, Guid filamentId, int amountMg, DateTimeOffset at)
    {
        var printerId = await _support.CreatePrinterAsync(user);
        await WithDbAsync(async db =>
        {
            var print = new Print
            {
                Title = "Usage",
                StartDate = at,
                Status = Print.PrintStatus.Success,
                ViewStatus = Print.PrintViewStatus.Private,
                PrinterId = printerId,
                CreatedById = user.Id,
                UpdatedById = user.Id,
            };
            db.Prints.Add(print);
            await db.SaveChangesAsync(Ct);
            db.PrintFilament.Add(new PrintFilament
            {
                PrintId = print.Id,
                FilamentId = filamentId,
                AmountMg = amountMg,
                Source = PrintFilament.SourceMeasurement.Weight,
                EstimatedSource = PrintFilament.SourceMeasurement.Weight,
            });
            await db.SaveChangesAsync(Ct);
            return 0;
        });
        // A print written around PrintService does not bump the cache version; do it here.
        factory.Services.GetRequiredService<ICacheVersionService>().InvalidateUserCache(user.Id);
    }

    private Task<HttpResponseMessage> GetAsync(string secret, string path) => SendAsync(secret, HttpMethod.Get, path, null);

    private Task<HttpResponseMessage> SendAsync(string secret, HttpMethod method, string path, string? body)
    {
        var request = new HttpRequestMessage(method, Base + path);
        request.Headers.Authorization = SpoolmanTestSupport.Basic(secret);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }
        return _client.SendAsync(request, Ct);
    }

    private async Task<T> RestAsync<T>(User user, HttpMethod method, string url)
    {
        var response = await RestRawAsync(user, method, url, null);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}");
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    private Task<HttpResponseMessage> RestRawAsync(User user, HttpMethod method, string url, object? body)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthUserId);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }
        return _client.SendAsync(request, Ct);
    }

    private async Task<T> WithDbAsync<T>(Func<PrintLogContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<PrintLogContext>());
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    private static string?[] Names(JsonElement spools) =>
        spools.EnumerateArray().Select(s => s.GetProperty("filament").GetProperty("name").GetString()).ToArray();

    /// <summary>
    /// The body is the Spoolman model: no key outside it, every required key present, and no
    /// null anywhere (exclude_none). Nested filament and vendor are checked the same way.
    /// </summary>
    private static void AssertModel(JsonElement json, string[] allowed, string[] required)
    {
        var keys = json.EnumerateObject().Select(p => p.Name).ToList();
        Assert.All(keys, k => Assert.Contains(k, allowed));
        Assert.All(required, k => Assert.Contains(k, keys));
        Assert.All(json.EnumerateObject(), p => Assert.NotEqual(JsonValueKind.Null, p.Value.ValueKind));

        if (json.TryGetProperty("filament", out var filament) && filament.ValueKind == JsonValueKind.Object)
        {
            AssertModel(filament, FilamentKeys, FilamentRequired);
        }
        if (json.TryGetProperty("vendor", out var vendor) && vendor.ValueKind == JsonValueKind.Object && allowed == FilamentKeys)
        {
            AssertModel(vendor, VendorKeys, VendorRequired);
        }
    }

    #endregion
}

/// <summary>Spoolman's own list semantics, pinned without a host.</summary>
public class SpoolmanQueryTests
{
    [Theory]
    [InlineData("a,b", new[] { "a", "b" })]
    [InlineData("\"Top shelf, Rack 3\",Garage", new[] { "\"Top shelf, Rack 3\"", "Garage" })]
    [InlineData("\"a,b", new[] { "\"a", "b" })]
    [InlineData("", new[] { "" })]
    [InlineData("a,", new[] { "a", "" })]
    public void SplitTerms_KeepsQuotedTermsWhole(string value, string[] expected) =>
        Assert.Equal(expected, SpoolmanQuery.SplitTerms(value));

    [Theory]
    [InlineData("Shelf 1", "shelf", true)]
    [InlineData("Shelf 1", "\"Shelf 1\"", true)]
    [InlineData("Shelf 10", "\"Shelf 1\"", false)]
    [InlineData(null, "", true)]
    [InlineData("Shelf 1", "", false)]
    [InlineData(null, "shelf", false)]
    [InlineData("Garage", "shelf,gar", true)]
    [InlineData("anything", null, true)]
    public void MatchesTerms(string? value, string? filter, bool expected) =>
        Assert.Equal(expected, SpoolmanQuery.MatchesTerms(value, filter));

    [Theory]
    [InlineData("name")]
    [InlineData("name:sideways")]
    [InlineData(":asc")]
    public void ParseSort_RejectsWhatSpoolmanRejects(string sort) =>
        Assert.Throws<SpoolmanQueryException>(() => SpoolmanQuery.ParseSort(sort));

    [Fact]
    public void ParseIds_RejectsNonIntegers() =>
        Assert.Throws<SpoolmanQueryException>(() => SpoolmanQuery.ParseIds("1,x", "filament.id"));

    [Theory]
    [InlineData("24.99", 24.99)]
    [InlineData(" $19.5 ", 19.5)]
    [InlineData("about twenty", null)]
    [InlineData("-3", null)]
    [InlineData("", null)]
    public void Price_IsReportedOnlyWhenItIsANumber(string value, double? expected) =>
        Assert.Equal(expected, SpoolmanInventoryService.Price(value));

    [Fact]
    public void DateTimes_AreSpoolmansIsoFormat()
    {
        Assert.Equal("2026-01-02T03:04:05Z", SpoolmanJson.FormatDateTime(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)));
        Assert.Equal("2026-01-02T03:04:05.123456Z", SpoolmanJson.FormatDateTime(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1_234_567)));
    }

    [Fact]
    public void VendorIds_ArePositive_StableAndCaseInsensitive()
    {
        var id = SpoolmanInventoryService.VendorId("Prusament");

        Assert.True(id > 0);
        Assert.Equal(id, SpoolmanInventoryService.VendorId(" prusament "));
        Assert.NotEqual(id, SpoolmanInventoryService.VendorId("Polymaker"));
    }
}
