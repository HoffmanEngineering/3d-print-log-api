using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.SpoolTags;
using PrintLogApi.SpoolTags;
using Xunit;

namespace PrintLogApi.IntegrationTests.SpoolTags;

/// <summary>
/// A tag's decoded reading and the chosen SpoolmanDB entry travel with the spool on create and
/// link (PUT): stored per tag, returned on the detail DTO, and counted as a SKU vote.
/// </summary>
public class FilamentTagReadingsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _httpClient;

    public FilamentTagReadingsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _httpClient = factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #region Readings

    [Fact]
    public async Task Create_WithAReading_StoresItOnTheTagAndReturnsIt()
    {
        var uid = RandomUid();
        var sku = RandomSku();

        var created = await CreateAsync([uid], [new() { Uid = uid, Reading = Reading(sku) }]);

        var tag = await LoadTagAsync(uid);
        Assert.Equal("snapmaker", tag.Format);
        Assert.NotNull(tag.DecodedJson);
        Assert.NotNull(tag.LastReadAt);

        var detail = await GetAsync(created.Id);
        var tagDetail = Assert.Single(detail.TagDetails!);
        Assert.Equal(uid, tagDetail.Uid);
        Assert.Equal("snapmaker", tagDetail.Format);
        Assert.Equal(sku, tagDetail.Reading!.Sku);
        Assert.Equal("PLA", tagDetail.Reading.MaterialType);
        Assert.NotNull(tagDetail.LastReadAt);
    }

    [Fact]
    public async Task Create_AReadingMatchesItsTagWhateverTheSeparators()
    {
        var uid = RandomUid();
        var formatted = string.Join(':', uid.Chunk(2).Select(c => new string(c))).ToLowerInvariant();

        await CreateAsync([uid], [new() { Uid = formatted, Reading = Reading(RandomSku()) }]);

        Assert.NotNull((await LoadTagAsync(uid)).DecodedJson);
    }

    [Fact]
    public async Task Create_AReadingForAUidNotInTagUids_IsIgnored()
    {
        var uid = RandomUid();
        var other = RandomUid();

        var created = await CreateAsync([uid], [new() { Uid = other, Reading = Reading(RandomSku()) }]);

        Assert.Null((await LoadTagAsync(uid)).DecodedJson);
        Assert.Equal(0, await CountTagsAsync(other));
        Assert.Null(Assert.Single((await GetAsync(created.Id)).TagDetails!).Reading);
    }

    [Fact]
    public async Task Create_AnImplausibleValue_IsStoredRemovedWithAWarning()
    {
        var uid = RandomUid();
        var reading = Reading(RandomSku());
        reading.HotendC = 900;

        var created = await CreateAsync([uid], [new() { Uid = uid, Reading = reading }]);

        var stored = Assert.Single((await GetAsync(created.Id)).TagDetails!).Reading!;
        Assert.Null(stored.HotendC);
        Assert.Contains("hotend-out-of-range", stored.Warnings);
    }

    [Fact]
    public async Task Create_AReadingOverTheSizeCap_SavesTheSpoolWithoutDecodedJson()
    {
        var uid = RandomUid();
        var reading = Reading(RandomSku());
        // Non-ASCII text is escaped to six bytes a character, so 20 extras overshoot 8 KB.
        for (var i = 0; i < 20; i++)
        {
            reading.Extras[$"{i:D2}{new string('é', 48)}"] = new string('é', 200);
        }

        var created = await CreateAsync([uid], [new() { Uid = uid, Reading = reading }]);

        var tag = await LoadTagAsync(uid);
        Assert.Null(tag.DecodedJson);
        Assert.Null(Assert.Single((await GetAsync(created.Id)).TagDetails!).Reading);
    }

    [Fact]
    public async Task Get_UnreadableStoredJson_ReturnsTheSpoolWithANullReading()
    {
        var uid = RandomUid();
        var created = await CreateAsync([uid], null);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var tag = await db.FilamentTags.SingleAsync(t => t.Uid == uid, Ct);
            tag.Format = "snapmaker";
            tag.DecodedJson = "{ this is not json";
            await db.SaveChangesAsync(Ct);
        }

        var response = await SendAsync(HttpMethod.Get, $"/api/Filaments/{created.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tagDetail = Assert.Single((await ReadAsync<FilamentDetailDto>(response)).TagDetails!);
        Assert.Equal(uid, tagDetail.Uid);
        Assert.Null(tagDetail.Reading);
    }

    [Fact]
    public async Task Get_TagDetailsAreOrderedByUid()
    {
        var uids = new[] { RandomUid(), RandomUid(), RandomUid() };

        var created = await CreateAsync([.. uids], null);

        Assert.Equal<IEnumerable<string?>>(uids.Order(), (await GetAsync(created.Id)).TagDetails!.Select(t => t.Uid));
    }

    #endregion

    #region Link via PUT

    [Fact]
    public async Task Put_AddingATagWithAReading_StoresTheReadingOnTheNewTag()
    {
        var existing = RandomUid();
        var added = RandomUid();
        var sku = RandomSku();
        var created = await CreateAsync([existing], null);

        var response = await PutAsync(created.Id, [existing, added], [new() { Uid = added, Reading = Reading(sku) }]);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Null((await LoadTagAsync(existing)).DecodedJson);
        var tag = await LoadTagAsync(added);
        Assert.Equal("snapmaker", tag.Format);
        Assert.Contains(sku, tag.DecodedJson);
    }

    [Fact]
    public async Task Put_AnExistingTagsReading_IsReplacedOnlyWhenANewOneIsSent()
    {
        var uid = RandomUid();
        var first = RandomSku();
        var second = RandomSku();
        var created = await CreateAsync([uid], [new() { Uid = uid, Reading = Reading(first) }]);

        await PutAsync(created.Id, [uid], null);
        Assert.Contains(first, (await LoadTagAsync(uid)).DecodedJson);

        await PutAsync(created.Id, [uid], [new() { Uid = uid, Reading = Reading(second) }]);
        var stored = (await LoadTagAsync(uid)).DecodedJson;
        Assert.Contains(second, stored);
        Assert.DoesNotContain(first, stored);
    }

    #endregion

    #region Catalog entry

    [Fact]
    public async Task Create_WithALiveCatalogEntry_StoresAndReturnsIt()
    {
        var entryId = await SeedEntryAsync();

        var created = await CreateAsync(null, null, entryId);

        Assert.Equal(entryId, created.CatalogEntryId);
        Assert.Equal(entryId, (await GetAsync(created.Id)).CatalogEntryId);
    }

    [Fact]
    public async Task Create_WithAnUnknownCatalogEntry_Returns400()
    {
        var response = await PostAsync(null, null, $"no-such-entry-{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithARemovedCatalogEntry_Returns400()
    {
        var entryId = await SeedEntryAsync(removed: true);

        var response = await PostAsync(null, null, entryId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_KeepsTheCatalogEntry_EvenWhenTheBodyNamesAnother()
    {
        var entryId = await SeedEntryAsync();
        var otherId = await SeedEntryAsync();
        var created = await CreateAsync(null, null, entryId);

        var current = await GetAsync(created.Id);
        current.CatalogEntryId = otherId;
        var response = await SendAsync(HttpMethod.Put, $"/api/Filaments/{created.Id}", current);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(entryId, (await GetAsync(created.Id)).CatalogEntryId);
    }

    [Fact]
    public async Task Put_WithoutACatalogEntry_KeepsIt()
    {
        var entryId = await SeedEntryAsync();
        var created = await CreateAsync(null, null, entryId);

        var current = await GetAsync(created.Id);
        current.CatalogEntryId = null;
        await SendAsync(HttpMethod.Put, $"/api/Filaments/{created.Id}", current);

        Assert.Equal(entryId, (await GetAsync(created.Id)).CatalogEntryId);
    }

    #endregion

    #region SKU votes

    [Fact]
    public async Task Create_WithAnEntryAndASkuReading_CastsOneVote()
    {
        var entryId = await SeedEntryAsync();
        var uid = RandomUid();
        var sku = RandomSku();

        await CreateAsync([uid], [new() { Uid = uid, Reading = Reading(sku) }], entryId);

        var vote = Assert.Single(await VotesAsync(sku));
        Assert.Equal(entryId, vote.CatalogEntryId);
        Assert.Equal("snapmaker", vote.Format);
        Assert.Equal(await TestUserIdAsync(), vote.UserId);
    }

    [Fact]
    public async Task Create_ASecondTimeByTheSameUser_UpdatesTheVote()
    {
        var first = await SeedEntryAsync();
        var second = await SeedEntryAsync();
        var sku = RandomSku();
        var uidA = RandomUid();
        var uidB = RandomUid();

        await CreateAsync([uidA], [new() { Uid = uidA, Reading = Reading(sku) }], first);
        await CreateAsync([uidB], [new() { Uid = uidB, Reading = Reading(sku) }], second);

        var vote = Assert.Single(await VotesAsync(sku));
        Assert.Equal(second, vote.CatalogEntryId);
    }

    [Fact]
    public async Task Create_AReadingWithoutASku_CastsNoVote()
    {
        var entryId = await SeedEntryAsync();
        var uid = RandomUid();

        await CreateAsync([uid], [new() { Uid = uid, Reading = Reading(null) }], entryId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        Assert.False(await db.FilamentCatalogSkus.AnyAsync(s => s.CatalogEntryId == entryId, Ct));
    }

    [Fact]
    public async Task Create_WithASkuButNoCatalogEntry_CastsNoVote()
    {
        var uid = RandomUid();
        var sku = RandomSku();

        await CreateAsync([uid], [new() { Uid = uid, Reading = Reading(sku) }]);

        Assert.Empty(await VotesAsync(sku));
    }

    #endregion

    #region Helpers

    private static string RandomUid() => Convert.ToHexString(RandomNumberGenerator.GetBytes(7));

    private static string RandomSku() => RandomNumberGenerator.GetInt32(100_000, 999_999_999).ToString();

    private static TagReading Reading(string? sku) => new()
    {
        Format = "snapmaker",
        DecoderVersion = 1,
        Brand = "Snapmaker",
        MaterialType = "PLA",
        Colors = ["FF0000"],
        DiameterMm = 1.75,
        NetWeightG = 1000,
        HotendC = 210,
        Sku = sku,
    };

    private async Task<string> SeedEntryAsync(bool removed = false)
    {
        var id = $"test_entry_{Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        db.FilamentCatalogEntries.Add(new FilamentCatalogEntry
        {
            Id = id,
            Manufacturer = "Test",
            Name = "Red",
            Material = "PLA",
            Density = 1.24,
            WeightG = 1000,
            DiameterMm = 1.75,
            ImportedAt = DateTime.UtcNow,
            RemovedAt = removed ? DateTime.UtcNow : null,
        });
        await db.SaveChangesAsync(Ct);
        return id;
    }

    private async Task<FilamentTag> LoadTagAsync(string uid)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.FilamentTags.AsNoTracking().SingleAsync(t => t.Uid == uid, Ct);
    }

    private async Task<int> CountTagsAsync(string uid)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.FilamentTags.CountAsync(t => t.Uid == uid, Ct);
    }

    private async Task<List<FilamentCatalogSku>> VotesAsync(string sku)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.FilamentCatalogSkus.AsNoTracking().Where(s => s.Sku == sku).ToListAsync(Ct);
    }

    private async Task<long> TestUserIdAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.Users.Where(u => u.OAuthUserId == IntegrationTestSeeder.TestUserOAuthId).Select(u => u.Id).SingleAsync(Ct);
    }

    private Task<HttpResponseMessage> PostAsync(List<string>? tagUids, List<TagReadingInputDto>? readings, string? catalogEntryId = null) =>
        SendAsync(HttpMethod.Post, "/api/Filaments", new AddFilamentDto
        {
            DisplayName = $"Read spool {Guid.NewGuid():N}",
            Brand = "Test Brand",
            MaterialType = "PLA",
            ColorName = "Red",
            ColorHex = "FF0000",
            DiameterMm = 1.75,
            InitialNominalWeightMg = 1_000_000,
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true,
            TagUids = tagUids,
            TagReadings = readings,
            CatalogEntryId = catalogEntryId,
        });

    private async Task<FilamentDetailDto> CreateAsync(List<string>? tagUids, List<TagReadingInputDto>? readings, string? catalogEntryId = null) =>
        await ReadAsync<FilamentDetailDto>(await PostAsync(tagUids, readings, catalogEntryId));

    private async Task<HttpResponseMessage> PutAsync(Guid id, List<string> tagUids, List<TagReadingInputDto>? readings)
    {
        var current = await GetAsync(id);
        current.TagUids = tagUids;
        current.TagReadings = readings;
        return await SendAsync(HttpMethod.Put, $"/api/Filaments/{id}", current);
    }

    private async Task<FilamentDetailDto> GetAsync(Guid id) =>
        await ReadAsync<FilamentDetailDto>(await SendAsync(HttpMethod.Get, $"/api/Filaments/{id}"));

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }
        return await _httpClient.SendAsync(request, Ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    #endregion
}
