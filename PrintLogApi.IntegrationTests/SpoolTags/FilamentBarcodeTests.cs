using System.Net;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.SpoolTags;

/// <summary>
/// Label barcodes travel with a new spool, can be remembered on or removed from an existing one,
/// and count as <c>barcode</c> SKU votes when the spool is linked to a catalog entry.
/// </summary>
public class FilamentBarcodeTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _httpClient;

    public FilamentBarcodeTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _httpClient = factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_WithBarcodes_StoresNormalized()
    {
        var upc = RandomUpc();

        var created = await CreateAsync([new() { Value = upc, Format = "UPC_A" }]);

        var barcode = Assert.Single((await GetAsync(created.Id)).Barcodes!);
        Assert.Equal("0" + upc, barcode.Value);
        Assert.Equal("ean13", barcode.Format);
    }

    [Fact]
    public async Task Create_WithBarcodesAndCatalogEntry_CastsBarcodeVotes()
    {
        var upc = RandomUpc();
        var entryId = await SeedEntryAsync();

        await CreateAsync([new() { Value = upc, Format = "UPC_A" }], entryId);

        var vote = Assert.Single(await VotesAsync("0" + upc));
        Assert.Equal("barcode", vote.Format);
        Assert.Equal(entryId, vote.CatalogEntryId);
    }

    [Fact]
    public async Task Create_WithBarcodesNoCatalogEntry_CastsNoVotes()
    {
        var upc = RandomUpc();

        await CreateAsync([new() { Value = upc, Format = "UPC_A" }]);

        Assert.Empty(await VotesAsync("0" + upc));
    }

    [Fact]
    public async Task Create_WithFiveBarcodes_Returns400()
    {
        var barcodes = Enumerable.Range(0, 5).Select(_ => new BarcodeInputDto { Value = RandomCode(), Format = "code128" }).ToList();

        var response = await PostAsync(barcodes);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateAndInvalidBarcodes_StoredOnceAndDropped()
    {
        var code = RandomCode();

        var created = await CreateAsync(
        [
            new() { Value = code, Format = "code128" },
            new() { Value = code.ToLowerInvariant(), Format = "CODE_128" },
            new() { Value = "https://bit.ly/x", Format = "qrcode" },
        ]);

        Assert.Equal(code, Assert.Single((await GetAsync(created.Id)).Barcodes!).Value);
    }

    [Fact]
    public async Task Remember_AddsBarcode_AndVotesWhenLinked()
    {
        var entryId = await SeedEntryAsync();
        var created = await CreateAsync([], entryId);
        var code = RandomCode();

        var response = await SendAsync(HttpMethod.Post, $"/api/Filaments/{created.Id}/barcodes", new BarcodeInputDto { Value = code, Format = "code128" });

        var dto = await ReadAsync<FilamentBarcodeDto>(response);
        Assert.Equal(code, dto.Value);
        Assert.True(dto.Id > 0);
        Assert.Equal(code, Assert.Single((await GetAsync(created.Id)).Barcodes!).Value);
        Assert.Equal(entryId, Assert.Single(await VotesAsync(code)).CatalogEntryId);
    }

    [Fact]
    public async Task Remember_OnUnlinkedSpool_CastsNoVote()
    {
        var created = await CreateAsync([]);
        var code = RandomCode();

        await ReadAsync<FilamentBarcodeDto>(await RememberAsync(created.Id, code));

        Assert.Empty(await VotesAsync(code));
    }

    [Fact]
    public async Task Remember_FifthBarcode_DropsOldest()
    {
        var first = RandomCode();
        var others = Enumerable.Range(0, 3).Select(_ => RandomCode()).ToList();
        var created = await CreateAsync([new() { Value = first, Format = "code128" }, .. others.Select(o => new BarcodeInputDto { Value = o, Format = "code128" })]);
        var fifth = RandomCode();

        await ReadAsync<FilamentBarcodeDto>(await RememberAsync(created.Id, fifth));

        var values = (await GetAsync(created.Id)).Barcodes!.Select(b => b.Value).ToList();
        Assert.Equal(4, values.Count);
        Assert.DoesNotContain(first, values);
        Assert.Contains(fifth, values);
    }

    [Fact]
    public async Task Remember_SameValueTwice_IsIdempotent()
    {
        var code = RandomCode();
        var created = await CreateAsync([new() { Value = code, Format = "code128" }]);

        var dto = await ReadAsync<FilamentBarcodeDto>(await RememberAsync(created.Id, code.ToLowerInvariant()));

        Assert.Equal(code, dto.Value);
        Assert.Single((await GetAsync(created.Id)).Barcodes!);
    }

    [Fact]
    public async Task Remember_Invalid_Returns400()
    {
        var created = await CreateAsync([]);

        var response = await RememberAsync(created.Id, "<b>nope</b>");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_barcode", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Remember_ForeignSpool_Returns404()
    {
        var created = await CreateAsync([]);

        var response = await SendAsync(HttpMethod.Post, $"/api/Filaments/{created.Id}/barcodes",
            new BarcodeInputDto { Value = RandomCode(), Format = "code128" }, IntegrationTestSeeder.SecondaryUserOAuthId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Remove_DeletesRow()
    {
        var created = await CreateAsync([new() { Value = RandomCode(), Format = "code128" }]);
        var barcodeId = Assert.Single((await GetAsync(created.Id)).Barcodes!).Id;

        var response = await SendAsync(HttpMethod.Delete, $"/api/Filaments/{created.Id}/barcodes/{barcodeId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty((await GetAsync(created.Id)).Barcodes!);
    }

    [Fact]
    public async Task Remove_ForeignSpool_Returns404()
    {
        var created = await CreateAsync([new() { Value = RandomCode(), Format = "code128" }]);
        var barcodeId = Assert.Single((await GetAsync(created.Id)).Barcodes!).Id;

        var response = await SendAsync(HttpMethod.Delete, $"/api/Filaments/{created.Id}/barcodes/{barcodeId}", null, IntegrationTestSeeder.SecondaryUserOAuthId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single((await GetAsync(created.Id)).Barcodes!);
    }

    [Fact]
    public async Task BarcodeWrites_BumpCacheVersion()
    {
        var created = await CreateAsync([]);
        var before = CacheVersion();

        await ReadAsync<FilamentBarcodeDto>(await RememberAsync(created.Id, RandomCode()));
        var afterRemember = CacheVersion();
        var barcodeId = Assert.Single((await GetAsync(created.Id)).Barcodes!).Id;
        await SendAsync(HttpMethod.Delete, $"/api/Filaments/{created.Id}/barcodes/{barcodeId}");

        Assert.NotEqual(before, afterRemember);
        Assert.NotEqual(afterRemember, CacheVersion());
    }

    [Fact]
    public async Task Put_LeavesBarcodesAlone()
    {
        var code = RandomCode();
        var created = await CreateAsync([new() { Value = code, Format = "code128" }]);
        var current = await GetAsync(created.Id);
        current.Barcodes = [];

        await ReadAsync<FilamentDetailDto>(await SendAsync(HttpMethod.Put, $"/api/Filaments/{created.Id}", current));

        Assert.Equal(code, Assert.Single((await GetAsync(created.Id)).Barcodes!).Value);
    }

    #region Helpers

    private static string RandomUpc() => string.Concat(Enumerable.Range(0, 12).Select(_ => Random.Shared.Next(10)));

    private static string RandomCode() => $"T{Guid.NewGuid():N}"[..12].ToUpperInvariant();

    private string CacheVersion()
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ICacheVersionService>().GetUserCacheVersion(IntegrationTestSeeder.TestUserId);
    }

    private async Task<string> SeedEntryAsync()
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
        });
        await db.SaveChangesAsync(Ct);
        return id;
    }

    private async Task<List<FilamentCatalogSku>> VotesAsync(string sku)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.FilamentCatalogSkus.AsNoTracking().Where(s => s.Sku == sku).ToListAsync(Ct);
    }

    private Task<HttpResponseMessage> RememberAsync(Guid id, string value) =>
        SendAsync(HttpMethod.Post, $"/api/Filaments/{id}/barcodes", new BarcodeInputDto { Value = value, Format = "code128" });

    private Task<HttpResponseMessage> PostAsync(List<BarcodeInputDto> barcodes, string? catalogEntryId = null) =>
        SendAsync(HttpMethod.Post, "/api/Filaments", new AddFilamentDto
        {
            DisplayName = $"Barcode spool {Guid.NewGuid():N}",
            Brand = "Test Brand",
            MaterialType = "PLA",
            ColorName = "Red",
            ColorHex = "FF0000",
            DiameterMm = 1.75,
            InitialNominalWeightMg = 1_000_000,
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true,
            Barcodes = barcodes,
            CatalogEntryId = catalogEntryId,
        });

    private async Task<FilamentDetailDto> CreateAsync(List<BarcodeInputDto> barcodes, string? catalogEntryId = null) =>
        await ReadAsync<FilamentDetailDto>(await PostAsync(barcodes, catalogEntryId));

    private async Task<FilamentDetailDto> GetAsync(Guid id) =>
        await ReadAsync<FilamentDetailDto>(await SendAsync(HttpMethod.Get, $"/api/Filaments/{id}"));

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, string? user = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, user ?? IntegrationTestSeeder.TestUserOAuthId);
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
