using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Tag UIDs on filament (#147): an RFID or NFC tag maps to one spool. UIDs are stored as
/// uppercase hex without separators, are unique per user, and resolve through
/// <c>GET /api/Filaments/by-tag/{uid}</c> whatever separators or case the reader used.
/// </summary>
public class FilamentTagsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _httpClient;

    public FilamentTagsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _httpClient = factory.CreateClient();
    }

    #region Normalization

    [Theory]
    [InlineData("04A23F11", "04A23F11")]
    [InlineData("04:A2:3f:11", "04A23F11")]
    [InlineData("04-a2-3f-11", "04A23F11")]
    [InlineData(" 04 A2 3F 11 ", "04A23F11")]
    [InlineData("04.a2.3f.11", "04A23F11")]
    [InlineData("04:A2:3F:11:22:33:44", "04A23F11223344")]
    public void Normalize_StripsSeparatorsAndUppercases(string input, string expected)
    {
        Assert.Equal(expected, TagUid.Normalize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("::")]
    [InlineData("04A23G11")] // not hex
    [InlineData("04A23F1")] // half a byte
    [InlineData("0x04A23F11")] // a prefix is not a separator
    public void Normalize_RejectsWhatIsNotAWholeNumberOfHexBytes(string input)
    {
        Assert.Null(TagUid.Normalize(input));
    }

    [Fact]
    public void Normalize_RejectsAUidLongerThanTheColumn()
    {
        Assert.Null(TagUid.Normalize(new string('A', TagUid.MaxLength + 2)));
        Assert.NotNull(TagUid.Normalize(new string('A', TagUid.MaxLength)));
    }

    #endregion

    #region Creating and editing

    [Fact]
    public async Task PostFilament_WithTagUids_StoresThemNormalized()
    {
        var uid = RandomUid();

        var created = await CreateFilamentAsync([WithColons(uid).ToLowerInvariant()]);

        Assert.Equal([uid], created.TagUids);
        Assert.Equal([uid], (await GetFilamentAsync(created.Id)).TagUids);
    }

    [Fact]
    public async Task PostFilament_WithoutTagUids_ReturnsAnEmptyList()
    {
        var created = await CreateFilamentAsync(null);

        Assert.NotNull(created.TagUids);
        Assert.Empty(created.TagUids);
    }

    [Fact]
    public async Task PostFilament_TheSameUidTwiceInOneBody_StoresItOnce()
    {
        var uid = RandomUid();

        var created = await CreateFilamentAsync([uid, WithColons(uid)]);

        Assert.Equal([uid], created.TagUids);
    }

    [Fact]
    public async Task PostFilament_InvalidUid_Returns400()
    {
        var response = await PostFilamentAsync(["not-a-tag"]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostFilament_TooManyTags_Returns400()
    {
        var uids = Enumerable.Range(0, TagUid.MaxPerFilament + 1).Select(_ => RandomUid()).ToList();

        var response = await PostFilamentAsync(uids);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PutFilament_WithTagUids_ReplacesTheSet()
    {
        var (kept, removed, added) = (RandomUid(), RandomUid(), RandomUid());
        var created = await CreateFilamentAsync([kept, removed]);

        var updated = await ReadAsync<FilamentDetailDto>(await PutTagsAsync(created.Id, [kept, WithColons(added)]));

        Assert.Equal(new[] { kept, added }.Order(), updated.TagUids!.Order());
        Assert.Equal(new[] { kept, added }.Order(), (await GetFilamentAsync(created.Id)).TagUids!.Order());
        Assert.Equal(HttpStatusCode.NotFound, (await GetByTagAsync(removed)).StatusCode);
    }

    [Fact]
    public async Task PutFilament_EmptyTagUids_RemovesThemAll()
    {
        var uid = RandomUid();
        var created = await CreateFilamentAsync([uid]);

        await ReadAsync<FilamentDetailDto>(await PutTagsAsync(created.Id, []));

        Assert.Empty((await GetFilamentAsync(created.Id)).TagUids!);
        Assert.Equal(HttpStatusCode.NotFound, (await GetByTagAsync(uid)).StatusCode);
    }

    [Fact]
    public async Task PutFilament_WithoutTagUids_KeepsTheTags()
    {
        // The Android app and older web builds send the whole filament without tagUids.
        var uid = RandomUid();
        var created = await CreateFilamentAsync([uid]);

        await ReadAsync<FilamentDetailDto>(await PutTagsAsync(created.Id, null));

        Assert.Equal([uid], (await GetFilamentAsync(created.Id)).TagUids);
    }

    [Fact]
    public async Task PutFilament_ItsOwnTagInAnotherFormat_IsNotAConflict()
    {
        var uid = RandomUid();
        var created = await CreateFilamentAsync([uid]);

        var response = await PutTagsAsync(created.Id, [WithColons(uid).ToLowerInvariant()]);

        Assert.Equal([uid], (await ReadAsync<FilamentDetailDto>(response)).TagUids);
    }

    [Fact]
    public async Task PutFilament_InvalidUid_Returns400()
    {
        var created = await CreateFilamentAsync(null);

        var response = await PutTagsAsync(created.Id, ["04A23F1"]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    #endregion

    #region Conflicts

    [Fact]
    public async Task PostFilament_UidOnAnotherSpool_Returns409NamingThatSpool()
    {
        var uid = RandomUid();
        var holder = await CreateFilamentAsync([uid]);

        var response = await PostFilamentAsync([WithColons(uid).ToLowerInvariant()]);

        await AssertConflictNamingAsync(response, uid, holder);
    }

    [Fact]
    public async Task PutFilament_UidOnAnotherSpool_Returns409AndChangesNothing()
    {
        var (taken, own) = (RandomUid(), RandomUid());
        var holder = await CreateFilamentAsync([taken]);
        var other = await CreateFilamentAsync([own]);

        var response = await PutTagsAsync(other.Id, [own, taken]);

        await AssertConflictNamingAsync(response, taken, holder);
        Assert.Equal([own], (await GetFilamentAsync(other.Id)).TagUids);
        Assert.Equal([taken], (await GetFilamentAsync(holder.Id)).TagUids);
    }

    [Fact]
    public async Task PostFilament_UidOnAnotherUsersSpool_IsAllowed()
    {
        // Unique per user, not globally: two accounts may each have scanned the same tag.
        var uid = RandomUid();
        var theirs = await CreateFilamentAsync([uid], IntegrationTestSeeder.SecondaryUserOAuthId);

        var mine = await CreateFilamentAsync([uid]);

        Assert.NotEqual(theirs.Id, mine.Id);
        Assert.Equal(mine.Id, (await ReadAsync<FilamentDetailDto>(await GetByTagAsync(uid))).Id);
        Assert.Equal(theirs.Id, (await ReadAsync<FilamentDetailDto>(
            await GetByTagAsync(uid, IntegrationTestSeeder.SecondaryUserOAuthId))).Id);
    }

    [Fact]
    public async Task DeleteFilament_FreesItsTags()
    {
        var uid = RandomUid();
        var deleted = await CreateFilamentAsync([uid]);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/api/Filaments/{deleted.Id}")).StatusCode);

        var next = await CreateFilamentAsync([uid]);

        Assert.Equal([uid], next.TagUids);
    }

    #endregion

    #region Lookup by tag

    [Fact]
    public async Task GetByTag_AnySeparatorOrCase_ResolvesTheSameSpool()
    {
        var uid = RandomUid();
        var created = await CreateFilamentAsync([uid]);

        foreach (var form in new[] { uid, uid.ToLowerInvariant(), WithColons(uid), WithColons(uid).ToLowerInvariant() })
        {
            var found = await ReadAsync<FilamentDetailDto>(await GetByTagAsync(form));
            Assert.Equal(created.Id, found.Id);
            Assert.Equal([uid], found.TagUids);
        }
    }

    [Fact]
    public async Task GetByTag_UnknownUid_Returns404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await GetByTagAsync(RandomUid())).StatusCode);
    }

    [Fact]
    public async Task GetByTag_AnotherUsersTag_Returns404()
    {
        var uid = RandomUid();
        await CreateFilamentAsync([uid], IntegrationTestSeeder.SecondaryUserOAuthId);

        Assert.Equal(HttpStatusCode.NotFound, (await GetByTagAsync(uid)).StatusCode);
    }

    [Fact]
    public async Task GetByTag_InvalidUid_Returns400()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await GetByTagAsync("XYZ")).StatusCode);
    }

    [Fact]
    public async Task GetByTag_Unauthenticated_Returns401()
    {
        var response = await _httpClient.GetAsync($"/api/Filaments/by-tag/{RandomUid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TagsAreStoredNormalizedWithTheirOwner()
    {
        var uid = RandomUid();
        var created = await CreateFilamentAsync([WithColons(uid).ToLowerInvariant()]);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var tag = await db.FilamentTags.SingleAsync(t => t.FilamentId == created.Id, TestContext.Current.CancellationToken);
        var owner = await db.Filaments.Where(f => f.Id == created.Id).Select(f => f.CreatedById).SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(uid, tag.Uid);
        Assert.Equal(owner, tag.UserId);
        Assert.Null(tag.Format);
    }

    #endregion

    #region Helpers

    private static string RandomUid() => Convert.ToHexString(RandomNumberGenerator.GetBytes(7));

    private static string WithColons(string uid) => string.Join(':', uid.Chunk(2).Select(c => new string(c)));

    private static async Task AssertConflictNamingAsync(HttpResponseMessage response, string uid, FilamentDetailDto holder)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var problem = doc.RootElement;
        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        Assert.Equal(uid, problem.GetProperty("tagUid").GetString());
        Assert.Equal(holder.Id, problem.GetProperty("filamentId").GetGuid());
        Assert.Equal(holder.DisplayName, problem.GetProperty("filamentDisplayName").GetString());
        Assert.Contains(holder.DisplayName!, problem.GetProperty("detail").GetString());
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, object? body = null, string userOAuthId = IntegrationTestSeeder.TestUserOAuthId)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, userOAuthId);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }
        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        return (await response.Content.ReadFromJsonAsync<T>(TestContext.Current.CancellationToken))!;
    }

    private Task<HttpResponseMessage> GetByTagAsync(string uid, string userOAuthId = IntegrationTestSeeder.TestUserOAuthId) =>
        SendAsync(HttpMethod.Get, $"/api/Filaments/by-tag/{Uri.EscapeDataString(uid)}", userOAuthId: userOAuthId);

    private async Task<FilamentDetailDto> GetFilamentAsync(Guid id) =>
        await ReadAsync<FilamentDetailDto>(await SendAsync(HttpMethod.Get, $"/api/Filaments/{id}"));

    private Task<HttpResponseMessage> PostFilamentAsync(List<string>? tagUids, string userOAuthId = IntegrationTestSeeder.TestUserOAuthId) =>
        SendAsync(HttpMethod.Post, "/api/Filaments", new AddFilamentDto
        {
            DisplayName = $"Tagged spool {Guid.NewGuid():N}",
            Brand = "Test Brand",
            MaterialType = "PLA",
            ColorName = "Green",
            ColorHex = "00FF00",
            DiameterMm = 1.75,
            InitialNominalWeightMg = 1_000_000,
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true,
            TagUids = tagUids,
        }, userOAuthId);

    private async Task<FilamentDetailDto> CreateFilamentAsync(List<string>? tagUids, string userOAuthId = IntegrationTestSeeder.TestUserOAuthId) =>
        await ReadAsync<FilamentDetailDto>(await PostFilamentAsync(tagUids, userOAuthId));

    private async Task<HttpResponseMessage> PutTagsAsync(Guid id, List<string>? tagUids)
    {
        var current = await GetFilamentAsync(id);
        current.TagUids = tagUids;
        return await SendAsync(HttpMethod.Put, $"/api/Filaments/{id}", current);
    }

    #endregion
}
