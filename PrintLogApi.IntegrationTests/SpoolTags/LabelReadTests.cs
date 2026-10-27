using System.Net;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.SpoolTags;
using PrintLogApi.SpoolTags.Labels;
using Xunit;

namespace PrintLogApi.IntegrationTests.SpoolTags;

/// <summary>
/// <c>POST /api/Filaments/label-reads</c>: a label's barcodes and OCR text become a reading, the
/// user's own spools it may be, and the catalog entry it may be.
/// </summary>
public class LabelReadTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _httpClient;

    public LabelReadTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _httpClient = factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OwnMatch_ByBarcode_ReturnsTheSpool()
    {
        var code = RandomCode();
        var id = await CreateSpoolAsync(barcode: code);

        var response = await ReadAsync(new LabelReadRequestDto { Barcodes = [new() { Value = code.ToLowerInvariant(), Format = "CODE_128" }] });

        var match = Assert.Single(response.OwnMatches);
        Assert.Equal(id, match.FilamentId);
        Assert.Equal(["same-barcode"], match.Reasons);
        Assert.Equal(1.0, match.Score);
    }

    [Fact]
    public async Task OwnMatch_NeverReturnsAnotherUsersSpool()
    {
        var code = RandomCode();
        await CreateSpoolAsync(barcode: code, user: IntegrationTestSeeder.SecondaryUserOAuthId);

        var response = await ReadAsync(new LabelReadRequestDto { Barcodes = [new() { Value = code, Format = "code128" }] });

        Assert.Empty(response.OwnMatches);
    }

    [Fact]
    public async Task OwnMatch_ByOcr_RanksTheSameColorNameFirst()
    {
        var material = $"PLA{Guid.NewGuid():N}"[..10].ToUpperInvariant();
        var black = await CreateSpoolAsync(material: material, color: "Black");
        var brick = await CreateSpoolAsync(material: material, color: "Brick Red");

        var response = await ReadAsync(new LabelReadRequestDto
        {
            OcrText = $"{material} Brick Red 1.75mm",
            Fields = new() { MaterialType = material },
        });

        Assert.Equal(brick, response.OwnMatches[0].FilamentId);
        Assert.Contains("same-color-name", response.OwnMatches[0].Reasons);
        // The same material alone is too weak to suggest, as for tags.
        Assert.DoesNotContain(response.OwnMatches, m => m.FilamentId == black);
    }

    [Fact]
    public async Task CatalogMatch_FromATrustedBarcodeVote()
    {
        var code = RandomCode();
        var entryId = await SeedEntryAsync($"Brand{Guid.NewGuid():N}"[..14], "PLA+ Silver");
        await VoteAsync(code, entryId, voters: 2);

        var response = await ReadAsync(new LabelReadRequestDto { Barcodes = [new() { Value = code, Format = "code128" }] });

        Assert.Equal(entryId, response.CatalogMatch?.Id);
        Assert.Equal(1.0, response.CatalogMatch!.Confidence);
    }

    [Fact]
    public async Task CatalogMatch_ByName_WhenTheBrandIsInTheText()
    {
        var brand = $"Brand{Guid.NewGuid():N}"[..14];
        var entryId = await SeedEntryAsync(brand, "PLA+ Silver");

        var response = await ReadAsync(new LabelReadRequestDto
        {
            OcrText = $"{brand.ToUpperInvariant()} PLA+ Silver 1kg",
            Fields = new() { MaterialType = "PLA+", WeightG = 1000, DiameterMm = 1.75 },
        });

        Assert.Equal(brand, response.Reading.Brand);
        Assert.Equal("PLA+ Silver", response.Reading.ColorName);
        Assert.Equal(entryId, response.CatalogMatch?.Id);
    }

    [Fact]
    public async Task Reading_MergesClientFieldsWithTheExtractedBrand()
    {
        var brand = $"Brand{Guid.NewGuid():N}"[..14];
        await SeedEntryAsync(brand, "Black");

        var response = await ReadAsync(new LabelReadRequestDto
        {
            OcrText = $"{brand} PETG",
            Fields = new() { MaterialType = "PETG", WeightG = 1000, DiameterMm = 1.75, HotendMinC = 230, HotendMaxC = 250, BedMinC = 80, BedMaxC = 90 },
        });

        Assert.Equal(new LabelReading(brand, "PETG", null, 1000, 1.75, 230, 250, 80, 90), response.Reading);
    }

    [Fact]
    public async Task InvalidBarcodes_AreDropped_AndTheRestEchoedNormalized()
    {
        var response = await ReadAsync(new LabelReadRequestDto
        {
            Barcodes =
            [
                new() { Value = "849344041764", Format = "UPC_A" },
                new() { Value = "https://bit.ly/x", Format = "QR_CODE" },
            ],
        });

        Assert.Equal([new LabelBarcode("0849344041764", "ean13")], response.Barcodes);
    }

    [Fact]
    public async Task OcrTextOver4000Characters_IsTruncatedNotRejected()
    {
        var response = await PostAsync(new LabelReadRequestDto { OcrText = new string('x', 5000) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void TheAction_CapsTheRequestBodyAt16Kb()
    {
        // TestServer has no body size feature, so the limit cannot be exercised over HTTP here;
        // Kestrel enforces it (413) in production.
        var action = typeof(PrintLogApi.Controllers.FilamentsController).GetMethod("ResolveLabelRead");

        var limit = Assert.Single(action!.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute), false));
        Assert.Equal(16_384, ((Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata)limit).MaxRequestBodySize);
    }

    [Fact]
    public async Task Unauthenticated_Returns401()
    {
        var response = await _httpClient.PostAsync("/api/Filaments/label-reads", JsonContent.Create(new LabelReadRequestDto()), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    #region Helpers

    private static string RandomCode() => $"L{Guid.NewGuid():N}"[..12].ToUpperInvariant();

    private async Task<Guid> CreateSpoolAsync(string? barcode = null, string material = "PLA", string color = "Red", string? user = null)
    {
        var dto = new AddFilamentDto
        {
            DisplayName = $"{color} spool",
            Brand = "Label Test",
            MaterialType = material,
            ColorName = color,
            ColorHex = "FF0000",
            DiameterMm = 1.75,
            InitialNominalWeightMg = 1_000_000,
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true,
            Barcodes = barcode == null ? null : [new() { Value = barcode, Format = "code128" }],
        };
        var response = await SendAsync("/api/Filaments", dto, user);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<FilamentDetailDto>(Ct))!.Id;
    }

    private async Task<string> SeedEntryAsync(string brand, string name)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var entry = CatalogMatchByNameTests.Entry($"e_{Guid.NewGuid():N}", name);
        entry.Manufacturer = brand;
        db.FilamentCatalogEntries.Add(entry);
        await db.SaveChangesAsync(Ct);
        return entry.Id;
    }

    private async Task VoteAsync(string code, string entryId, int voters)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        for (var i = 0; i < voters; i++)
        {
            var user = new User { OAuthUserId = $"auth0|{Guid.NewGuid()}" };
            db.Users.Add(user);
            await db.SaveChangesAsync(Ct);
            db.FilamentCatalogSkus.Add(new FilamentCatalogSku { Format = "barcode", Sku = code, CatalogEntryId = entryId, UserId = user.Id, CreatedAt = DateTime.UtcNow });
        }
        await db.SaveChangesAsync(Ct);
    }

    private async Task<LabelReadResponse> ReadAsync(LabelReadRequestDto body)
    {
        var response = await PostAsync(body);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<LabelReadResponse>(Ct))!;
    }

    private Task<HttpResponseMessage> PostAsync(LabelReadRequestDto body) => SendAsync("/api/Filaments/label-reads", body);

    private Task<HttpResponseMessage> SendAsync(string url, object body, string? user = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, body.GetType()) };
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, user ?? IntegrationTestSeeder.TestUserOAuthId);
        return _httpClient.SendAsync(request, Ct);
    }

    #endregion
}
