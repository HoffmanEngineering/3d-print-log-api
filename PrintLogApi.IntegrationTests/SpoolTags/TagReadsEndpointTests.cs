using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.SpoolTags;
using PrintLogApi.SpoolTags;
using Xunit;

namespace PrintLogApi.IntegrationTests.SpoolTags;

/// <summary>POST /api/Filaments/tag-reads: resolves a tag read to a linked spool, or to a decoded reading with suggestions.</summary>
public class TagReadsEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string Url = "/api/Filaments/tag-reads";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _httpClient;

    public TagReadsEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _httpClient = factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string RandomUid() => Convert.ToHexString(RandomNumberGenerator.GetBytes(7));

    private static TagReadRequest Fixture(string name, string uid)
    {
        var request = TagFixtures.Load(name);
        request.Uid = uid;
        return request;
    }

    [Fact]
    public async Task UnknownSnapmakerTag_ReturnsTheReadingAndSuggestions()
    {
        var uid = RandomUid();
        var first = await ReadAsync(Fixture("snapmaker-805BEC3E", uid));
        Assert.Equal("unknown", first.GetProperty("status").GetString());
        var reading = first.GetProperty("reading");
        Assert.Equal("PLA", reading.GetProperty("materialType").GetString());
        Assert.Equal("900001", reading.GetProperty("sku").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("catalogMatch").ValueKind);
        Assert.Equal(JsonValueKind.Null, first.GetProperty("filament").ValueKind);

        // An untagged PLA in the reading's brand and color, owned by this user.
        var filament = await CreateFilamentAsync(
            reading.GetProperty("brand").GetString(),
            reading.GetProperty("colors")[0].GetString(),
            tagUids: null);

        var second = await ReadAsync(Fixture("snapmaker-805BEC3E", uid));

        Assert.Equal("unknown", second.GetProperty("status").GetString());
        var suggestions = second.GetProperty("suggestions").EnumerateArray().ToList();
        Assert.Contains(suggestions, s => s.GetProperty("filamentId").GetGuid() == filament.Id);
    }

    [Fact]
    public async Task KnownTag_IsLinked_AndItsStoredReadingIsRefreshed()
    {
        var uid = RandomUid();
        var filament = await CreateFilamentAsync("Polymaker", "FF0000", [uid]);
        var before = await LoadAsync(uid, filament.Id);

        var result = await ReadAsync(Fixture("snapmaker-805BEC3E", uid));

        Assert.Equal("linked", result.GetProperty("status").GetString());
        Assert.Equal(filament.Id, result.GetProperty("filament").GetProperty("id").GetGuid());

        var after = await LoadAsync(uid, filament.Id);
        Assert.Equal("snapmaker", after.Tag.Format);
        Assert.Equal(1, after.Tag.DecoderVersion);
        Assert.NotNull(after.Tag.LastReadAt);
        Assert.Equal("900001", TagReadingJson.Deserialize(after.Tag.DecodedJson)!.Sku);
        Assert.Equal(before.Filament.Brand, after.Filament.Brand);
        Assert.Equal(before.Filament.UpdatedDate, after.Filament.UpdatedDate);
    }

    [Fact]
    public async Task KnownTag_WithoutMemory_IsLinked_AndOnlyLastReadAtChanges()
    {
        var uid = RandomUid();
        var filament = await CreateFilamentAsync("Polymaker", "FF0000", [uid]);
        await ReadAsync(Fixture("snapmaker-805BEC3E", uid));
        var stored = (await LoadAsync(uid, filament.Id)).Tag;

        var result = await ReadAsync(new TagReadRequest { Uid = uid });

        Assert.Equal("linked", result.GetProperty("status").GetString());
        var tag = (await LoadAsync(uid, filament.Id)).Tag;
        Assert.Equal(stored.DecodedJson, tag.DecodedJson);
        Assert.Equal("snapmaker", tag.Format);
        Assert.True(tag.LastReadAt >= stored.LastReadAt);
    }

    [Fact]
    public async Task AnotherUsersTag_IsUnknown_NeverLinked_SuggestedOrTouched()
    {
        var uid = RandomUid();
        var other = await CreateOtherUserAsync();

        // The reading's brand and color, so any leak through suggestions would show.
        var probe = await ReadAsync(Fixture("snapmaker-805BEC3E", uid), other);
        var reading = probe.GetProperty("reading");
        var owners = await CreateFilamentAsync(
            reading.GetProperty("brand").GetString(),
            reading.GetProperty("colors")[0].GetString(),
            [uid]);
        var lookalike = await CreateFilamentAsync(
            reading.GetProperty("brand").GetString(),
            reading.GetProperty("colors")[0].GetString(),
            tagUids: null);
        var before = (await LoadAsync(uid, owners.Id)).Tag;

        var result = await ReadAsync(Fixture("snapmaker-805BEC3E", uid), other);

        Assert.Equal("unknown", result.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("filament").ValueKind);
        var suggested = result.GetProperty("suggestions").EnumerateArray().Select(s => s.GetProperty("filamentId").GetGuid()).ToList();
        Assert.DoesNotContain(owners.Id, suggested);
        Assert.DoesNotContain(lookalike.Id, suggested);

        var after = (await LoadAsync(uid, owners.Id)).Tag;
        Assert.Equal(before.LastReadAt, after.LastReadAt);
        Assert.Equal(before.DecodedJson, after.DecodedJson);
        Assert.Equal(before.Format, after.Format);
    }

    [Fact]
    public async Task UnrecognizedMemory_IsUnknown_AndStoresOneFailureRow()
    {
        var uid = RandomUid();
        var request = new TagReadRequest
        {
            Uid = uid,
            Techs = ["android.nfc.tech.MifareClassic", "android.nfc.tech.NfcA"],
            Memory = new TagMemoryDto { Kind = "classic", Data = Convert.ToBase64String(RandomNumberGenerator.GetBytes(1024)) },
        };

        var result = await ReadAsync(request);

        Assert.Equal("unknown", result.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("reading").ValueKind);
        var row = Assert.Single(await FailuresAsync(uid));
        Assert.Equal("classic", row.MemoryKind);
        Assert.False(string.IsNullOrEmpty(row.Error));
        Assert.Contains("MifareClassic", row.Techs);
    }

    [Fact]
    public async Task BlankTag_IsUnknown_WithNoReadingAndNoFailureRow()
    {
        var uid = RandomUid();

        var result = await ReadAsync(Fixture("blank-ev1-0416C55A061F91", uid));

        Assert.Equal("unknown", result.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("reading").ValueKind);
        Assert.Empty(await FailuresAsync(uid));
    }

    [Fact]
    public async Task PartialRead_AsksTheUserToHoldStill_AndStoresNothing()
    {
        var uid = RandomUid();
        var request = Fixture("snapmaker-805BEC3E", uid);
        request.Memory!.Unreadable = [0];

        var result = await ReadAsync(request);

        Assert.Equal("unknown", result.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("reading").ValueKind);
        Assert.Equal("hold-still", result.GetProperty("hint").GetString());
        Assert.Empty(await FailuresAsync(uid));
    }

    [Fact]
    public async Task NothingReadable_IsUnknownWithNoHintAndNoFailureRow()
    {
        var uid = RandomUid();
        var request = Fixture("snapmaker-805BEC3E", uid);
        request.Memory!.Unreadable = Enumerable.Range(0, 16).ToList();

        var result = await ReadAsync(request);

        Assert.Equal("unknown", result.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("hint").ValueKind);
        Assert.Empty(await FailuresAsync(uid));
    }

    [Fact]
    public async Task DecodedRead_HasNoHint()
    {
        var result = await ReadAsync(Fixture("snapmaker-805BEC3E", RandomUid()));

        Assert.Equal(JsonValueKind.Null, result.GetProperty("hint").ValueKind);
    }

    [Fact]
    public async Task InvalidUid_Returns400()
    {
        var response = await PostAsync(new TagReadRequest { Uid = "xyz" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ClassicMemoryOfTheWrongSize_Returns400()
    {
        var response = await PostAsync(new TagReadRequest
        {
            Uid = RandomUid(),
            Memory = new TagMemoryDto { Kind = "classic", Data = Convert.ToBase64String(new byte[1023]) },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OversizedMemory_Returns400()
    {
        var response = await PostAsync(new TagReadRequest
        {
            Uid = RandomUid(),
            Memory = new TagMemoryDto { Kind = "nfcv", Data = new string('A', 20_000) },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void TheAction_CapsTheRequestBodyAt16Kb()
    {
        // TestServer has no body size feature, so the limit cannot be exercised over HTTP here;
        // Kestrel enforces it (413) in production.
        var action = typeof(PrintLogApi.Controllers.FilamentsController).GetMethod("ResolveTagRead");

        var limit = Assert.Single(action!.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute), false));
        Assert.Equal(16_384, ((Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata)limit).MaxRequestBodySize);
    }

    [Fact]
    public async Task Anonymous_Returns401()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(Url, new TagReadRequest { Uid = RandomUid() }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private Task<HttpResponseMessage> PostAsync(TagReadRequest body, string? oauthUserId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url) { Content = JsonContent.Create(body) };
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, oauthUserId ?? IntegrationTestSeeder.TestUserOAuthId);
        return _httpClient.SendAsync(request, Ct);
    }

    private async Task<JsonElement> ReadAsync(TagReadRequest body, string? oauthUserId = null)
    {
        var response = await PostAsync(body, oauthUserId);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<FilamentDetailDto> CreateFilamentAsync(string? brand, string? colorHex, List<string>? tagUids)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments")
        {
            Content = JsonContent.Create(new AddFilamentDto
            {
                DisplayName = $"Tag read spool {Guid.NewGuid():N}",
                Brand = brand,
                MaterialType = "PLA",
                ColorName = "Test",
                ColorHex = colorHex,
                Colors = colorHex is null ? [] : [colorHex],
                DiameterMm = 1.75,
                InitialNominalWeightMg = 1_000_000,
                MaterialDensityGramPerCubicCm = 1.24,
                IsActive = true,
                TagUids = tagUids,
            }),
        };
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _httpClient.SendAsync(request, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<FilamentDetailDto>(Web, Ct))!;
    }

    private async Task<string> CreateOtherUserAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = new User { OAuthUserId = $"auth0|tag-reads-other-{Guid.NewGuid():N}", ViewStatus = User.ProfileViewStatus.Public };
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);
        return user.OAuthUserId;
    }

    private async Task<(FilamentTag Tag, Filament Filament)> LoadAsync(string uid, Guid filamentId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var tag = await db.FilamentTags.AsNoTracking().SingleAsync(t => t.Uid == uid && t.FilamentId == filamentId, Ct);
        var filament = await db.Filaments.AsNoTracking().SingleAsync(f => f.Id == filamentId, Ct);
        return (tag, filament);
    }

    private async Task<List<TagReadFailure>> FailuresAsync(string uid)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.TagReadFailures.AsNoTracking().Where(f => f.Uid == uid).ToListAsync(Ct);
    }
}
