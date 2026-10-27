using System.Net;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.SpoolTags;
using PrintLogApi.SpoolTags.Labels;
using PrintLogApi.SpoolTags.Matching;
using Xunit;

namespace PrintLogApi.IntegrationTests.SpoolTags;

/// <summary>A label gives a brand, material and color name, never a hex color, so it matches by name.</summary>
public class CatalogMatchByNameTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<CatalogTestHost> HostWith(params FilamentCatalogEntry[] entries)
    {
        var host = new CatalogTestHost(importEnabled: false);
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        db.FilamentCatalogEntries.AddRange(entries);
        await db.SaveChangesAsync(Ct);
        return host;
    }

    internal static FilamentCatalogEntry Entry(string id, string name, int weight = 1000, bool removed = false) => new()
    {
        Id = id,
        Manufacturer = "SUNLU",
        Name = name,
        Material = "PLA+",
        Density = 1.24,
        WeightG = weight,
        SpoolWeightG = 160,
        DiameterMm = 1.75,
        ExtruderTemp = 210,
        ExtruderTempMin = 200,
        ExtruderTempMax = 230,
        BedTemp = 60,
        ImportedAt = DateTime.UtcNow,
        RemovedAt = removed ? DateTime.UtcNow : null,
    };

    private static async Task<CatalogMatch?> Match(CatalogTestHost host, LabelReading reading)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<FilamentCatalogMatcher>().MatchByNameAsync(reading, Ct);
    }

    private static LabelReading Reading(string? brand = "sunlu", string? material = "PLA+", string? color = "Silver", int? weight = null) =>
        new(brand, material, color, weight, 1.75, null, null, null, null);

    [Fact]
    public async Task BrandMaterialAndColorName_MatchTheEntry_WithItsTemperatures()
    {
        await using var host = await HostWith(Entry("s", "PLA+ Silver"), Entry("b", "PLA+ Black"));

        var match = await Match(host, Reading());

        Assert.Equal("s", match?.Id);
        Assert.Equal(200, match!.ExtruderTempMinC);
        Assert.Equal(230, match.ExtruderTempMaxC);
        Assert.Equal(210, match.ExtruderTempC);
        Assert.Equal(60, match.BedTempC);
        Assert.Equal(160, match.SpoolWeightG);
    }

    [Fact]
    public async Task NoBrand_IsNoMatch()
    {
        await using var host = await HostWith(Entry("s", "PLA+ Silver"));

        Assert.Null(await Match(host, Reading(brand: null)));
    }

    [Fact]
    public async Task AColorNoEntryNames_IsNoMatch()
    {
        await using var host = await HostWith(Entry("s", "PLA+ Silver"));

        Assert.Null(await Match(host, Reading(color: "Gold")));
    }

    [Fact]
    public async Task Weight_BreaksATie()
    {
        await using var host = await HostWith(Entry("small", "PLA+ Silver", weight: 250), Entry("big", "PLA+ Silver", weight: 1000));

        Assert.Equal("big", (await Match(host, Reading(weight: 1000)))?.Id);
        Assert.Equal("small", (await Match(host, Reading(weight: 250)))?.Id);
    }

    [Fact]
    public async Task ARemovedEntry_IsNeverReturned()
    {
        await using var host = await HostWith(Entry("s", "PLA+ Silver", removed: true));

        Assert.Null(await Match(host, Reading()));
    }
}

public class CatalogMatchEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _httpClient;

    public CatalogMatchEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _httpClient = factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NoMatch_Returns204()
    {
        var response = await PostAsync(new CatalogMatchRequestDto { Brand = $"Nobody {Guid.NewGuid():N}", MaterialType = "PLA", ColorName = "Red" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task AMatch_Returns200WithTemperatures()
    {
        var brand = $"Brand{Guid.NewGuid():N}"[..14];
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var entry = CatalogMatchByNameTests.Entry($"e_{Guid.NewGuid():N}", "PLA+ Silver");
            entry.Manufacturer = brand;
            db.FilamentCatalogEntries.Add(entry);
            await db.SaveChangesAsync(Ct);
        }

        var response = await PostAsync(new CatalogMatchRequestDto { Brand = brand, MaterialType = "PLA+", ColorName = "silver", NetWeightG = 1000 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync(Ct);
        // The same shape a tag read's catalogMatch has, which the web app already reads.
        Assert.Contains("\"densityGramPerCubicCm\":1.24", json);
        Assert.Contains("\"source\":\"SpoolmanDB\"", json);
        var match = System.Text.Json.JsonSerializer.Deserialize<CatalogMatchDto>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal("PLA+ Silver", match!.Name);
        Assert.Equal(230, match.ExtruderTempMaxC);
        Assert.Equal(60, match.BedTempC);
        // A barcode-only read has no OCR text, so the form takes these from the match.
        Assert.Equal("PLA+", match.Material);
        Assert.Equal(1000, match.WeightG);
        Assert.Equal(1.75, match.DiameterMm);
    }

    private Task<HttpResponseMessage> PostAsync(CatalogMatchRequestDto body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments/catalog-match") { Content = JsonContent.Create(body) };
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        return _httpClient.SendAsync(request, Ct);
    }
}
