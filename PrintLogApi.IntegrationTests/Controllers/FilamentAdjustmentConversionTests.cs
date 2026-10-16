using System.Net;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Remaining-weight figures sum adjustments by AmountMg. A Length or Volume adjustment is
/// converted to AmountMg on save, and one that can't be converted (no usable density, or a
/// Length on a spool with no diameter) used to be saved with AmountMg null and silently
/// dropped out of every figure (#232). The REST paths now reject it, as the MCP
/// adjust_material_remaining tool already did.
/// </summary>
public class FilamentAdjustmentConversionTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string UnconvertibleMessage =
        "This material is missing the density/diameter needed to convert the adjustment to a weight.";

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _httpClient;

    public FilamentAdjustmentConversionTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _httpClient = factory.CreateClient();
    }

    [Fact]
    public async Task UpdateFilament_LengthAdjustmentWithoutDiameter_Returns400AndIsNotSaved()
    {
        var filament = await CreateFilamentAsync("No Diameter Length Adjustment", diameterMm: null);

        var response = await PutAsync(filament, [LengthAdjustment(filament.Id, 10)]);

        await AssertUnconvertibleAsync(response);
        var reread = await GetAsync(filament.Id);
        Assert.Empty(reread.FilamentAdjustments!);
    }

    [Fact]
    public async Task AddFilament_LengthAdjustmentWithoutDiameter_Returns400AndCreatesNothing()
    {
        const string name = "Create With Unconvertible Adjustment 232";
        var response = await PostAsync(NewFilament(name, diameterMm: null, adjustments:
            [LengthAdjustment(Guid.Empty, 10)]));

        await AssertUnconvertibleAsync(response);
        Assert.Equal(0, await CountFilamentsNamedAsync(name));
    }

    [Fact]
    public async Task UpdateFilament_LengthAdjustmentOnCategoryWithoutDiameter_Returns400()
    {
        // Resin tracks no diameter at all, so a length can never become a weight.
        var filament = await CreateFilamentAsync("Resin Length Adjustment", diameterMm: null, category: "resin");

        var response = await PutAsync(filament, [LengthAdjustment(filament.Id, 10)]);

        await AssertUnconvertibleAsync(response);
        Assert.Empty((await GetAsync(filament.Id)).FilamentAdjustments!);
    }

    [Fact]
    public async Task UpdateFilament_LengthAdjustmentWithZeroDensity_Returns400()
    {
        // Density 0 converts any length to 0 mg: saved, it would count for nothing.
        var filament = await CreateFilamentAsync("Zero Density Length Adjustment", density: 0, nominalWeightMg: null);

        var response = await PutAsync(filament, [LengthAdjustment(filament.Id, 10)]);

        await AssertUnconvertibleAsync(response);
        Assert.Empty((await GetAsync(filament.Id)).FilamentAdjustments!);
    }

    [Fact]
    public async Task UpdateFilament_VolumeAdjustmentWithZeroDensity_Returns400()
    {
        var filament = await CreateFilamentAsync("Zero Density Volume Adjustment", density: 0, nominalWeightMg: null);

        var response = await PutAsync(filament, [VolumeAdjustment(filament.Id, 10)]);

        await AssertUnconvertibleAsync(response);
        Assert.Empty((await GetAsync(filament.Id)).FilamentAdjustments!);
    }

    [Fact]
    public async Task UpdateFilament_VolumeAdjustmentWithoutDiameter_ConvertsFromDensityAlone()
    {
        // Volume to weight needs only the density. The missing diameter used to skip the whole
        // conversion, which saved this row with AmountMg null too.
        var filament = await CreateFilamentAsync("No Diameter Volume Adjustment", diameterMm: null);

        var response = await PutAsync(filament, [VolumeAdjustment(filament.Id, -100)]);

        await AssertSuccessAsync(response);
        var row = Assert.Single((await GetAsync(filament.Id)).FilamentAdjustments!);
        Assert.Equal(-124_000, row.AmountMg); // 100 ml x 1.24 g/cm3
        Assert.Null(row.LengthInM);
    }

    [Fact]
    public async Task UpdateFilament_WeightAdjustmentWithoutDiameter_IsSaved()
    {
        // A weight needs no conversion, so nothing about the material can make it unusable.
        var filament = await CreateFilamentAsync("No Diameter Weight Adjustment", diameterMm: null);

        var response = await PutAsync(filament, [WeightAdjustment(filament.Id, -50_000)]);

        await AssertSuccessAsync(response);
        var reread = await GetAsync(filament.Id);
        Assert.Equal(-50_000, Assert.Single(reread.FilamentAdjustments!).AmountMg);
        Assert.Equal(950_000, reread.FilamentRemaining);
    }

    [Fact]
    public async Task UpdateFilament_LengthAdjustmentWithDiameter_IsConverted()
    {
        var filament = await CreateFilamentAsync("Diameter Length Adjustment");

        var response = await PutAsync(filament, [LengthAdjustment(filament.Id, -10)]);

        await AssertSuccessAsync(response);
        var row = Assert.Single((await GetAsync(filament.Id)).FilamentAdjustments!);
        Assert.NotNull(row.AmountMg);
        Assert.True(row.AmountMg < 0);
    }

    [Fact]
    public async Task UpdateFilament_ClientSuppliedAmountOnUnconvertibleLength_IsStillRejected()
    {
        // AmountMg is derived for a Length row. A client sending one alongside must not get a
        // made-up weight past the check by way of the conversion's early return.
        var filament = await CreateFilamentAsync("Client Amount On Length 232", diameterMm: null);
        var adjustment = LengthAdjustment(filament.Id, 10);
        adjustment.AmountMg = -30_000;

        var response = await PutAsync(filament, [adjustment]);

        await AssertUnconvertibleAsync(response);
    }

    #region Re-deriving stored adjustments when the material changes

    [Fact]
    public async Task UpdateFilament_DensityChange_RederivesAdjustmentsSentInTheRequest()
    {
        var filament = await CreateFilamentAsync("Density Change Resent 232");
        await AssertSuccessAsync(await PutAsync(filament, [LengthAdjustment(filament.Id, -10)]));
        var stored = Assert.Single((await GetAsync(filament.Id)).FilamentAdjustments!);

        // The web app re-sends every adjustment it shows, with its stored fields.
        var response = await PutAsync(filament, [stored], density: 2.48);

        await AssertSuccessAsync(response);
        var rederived = Assert.Single((await GetAsync(filament.Id)).FilamentAdjustments!);
        Assert.Equal(stored.Id, rederived.Id);
        // Twice the density, twice the weight, give or take the rounding to whole milligrams.
        Assert.InRange(rederived.AmountMg!.Value, stored.AmountMg!.Value * 2 - 1, stored.AmountMg.Value * 2 + 1);
    }

    [Fact]
    public async Task UpdateFilament_AdjustmentsLeftOutOfTheRequest_AreRemoved()
    {
        // Pins current behavior: the adjustment list in a PUT replaces the stored one, so no
        // stored adjustment can be left behind un-re-derived. A client that omits the list
        // deletes every adjustment.
        var filament = await CreateFilamentAsync("Adjustments Omitted 232");
        await AssertSuccessAsync(await PutAsync(filament, [LengthAdjustment(filament.Id, -10)]));

        var response = await PutAsync(filament, [], density: 2.48);

        await AssertSuccessAsync(response);
        Assert.Empty((await GetAsync(filament.Id)).FilamentAdjustments!);
    }

    #endregion

    #region Adjustments saved before the check existed

    [Fact]
    public async Task UpdateFilament_UnchangedLegacyUnconvertibleAdjustment_DoesNotBlockUnrelatedEdits()
    {
        var filament = await CreateFilamentAsync("Legacy Bad Adjustment Rename 232", diameterMm: null);
        var legacyId = await InsertLegacyLengthAdjustmentAsync(filament.Id, 10);
        var stored = Assert.Single((await GetAsync(filament.Id)).FilamentAdjustments!);
        filament.DisplayName = "Legacy Bad Adjustment Renamed 232";

        var response = await PutAsync(filament, [stored]);

        await AssertSuccessAsync(response);
        var reread = await GetAsync(filament.Id);
        Assert.Equal("Legacy Bad Adjustment Renamed 232", reread.DisplayName);
        var kept = Assert.Single(reread.FilamentAdjustments!);
        Assert.Equal(legacyId, kept.Id);
        Assert.Null(kept.AmountMg);
    }

    [Fact]
    public async Task UpdateFilament_UnchangedLegacyAdjustment_IsConvertedOnceTheDiameterIsSet()
    {
        var filament = await CreateFilamentAsync("Legacy Bad Adjustment Fixed 232", diameterMm: null);
        await InsertLegacyLengthAdjustmentAsync(filament.Id, -10);
        var stored = Assert.Single((await GetAsync(filament.Id)).FilamentAdjustments!);

        var response = await PutAsync(filament, [stored], diameterMm: 1.75);

        await AssertSuccessAsync(response);
        var row = Assert.Single((await GetAsync(filament.Id)).FilamentAdjustments!);
        Assert.NotNull(row.AmountMg);
        Assert.True(row.AmountMg < 0);
    }

    [Fact]
    public async Task UpdateFilament_EditedLegacyUnconvertibleAdjustment_Returns400()
    {
        var filament = await CreateFilamentAsync("Legacy Bad Adjustment Edited 232", diameterMm: null);
        await InsertLegacyLengthAdjustmentAsync(filament.Id, 10);
        var stored = Assert.Single((await GetAsync(filament.Id)).FilamentAdjustments!);
        stored.LengthInM = 20;

        var response = await PutAsync(filament, [stored]);

        await AssertUnconvertibleAsync(response);
    }

    [Fact]
    public async Task UpdateFilament_ClearingTheDiameterUnderAConvertedLengthAdjustment_Returns400()
    {
        // The row converted when it was saved; clearing the diameter would turn it into one
        // that silently stops counting, which is the same bug by another route.
        var filament = await CreateFilamentAsync("Diameter Cleared Under Adjustment 232");
        await AssertSuccessAsync(await PutAsync(filament, [LengthAdjustment(filament.Id, -10)]));
        var stored = Assert.Single((await GetAsync(filament.Id)).FilamentAdjustments!);

        var response = await PutAsync(filament, [stored], clearDiameter: true);

        await AssertUnconvertibleAsync(response);
        Assert.Equal(1.75, (await GetAsync(filament.Id)).DiameterMm);
    }

    #endregion

    /// <summary>
    /// Writes the row the REST paths used to save before #232: a Length adjustment whose weight
    /// could not be derived, so AmountMg is null.
    /// </summary>
    private async Task<Guid> InsertLegacyLengthAdjustmentAsync(Guid filamentId, double lengthInM)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var row = new FilamentAdjustment
        {
            FilamentId = filamentId,
            Source = FilamentAdjustment.SourceMeasurement.Length,
            LengthInM = lengthInM,
            CreatedById = IntegrationTestSeeder.TestUserId,
            UpdatedById = IntegrationTestSeeder.TestUserId,
        };
        db.FilamentAdjustments.Add(row);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return row.Id;
    }

    private async Task<int> CountFilamentsNamedAsync(string displayName)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.Filaments.CountAsync(f => f.DisplayName == displayName, TestContext.Current.CancellationToken);
    }

    private static async Task AssertSuccessAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"Expected success, got {(int)response.StatusCode}. Body: {body}");
    }

    private static FilamentAdjustmentDto LengthAdjustment(Guid filamentId, double lengthInM) => new()
    {
        FilamentId = filamentId,
        Source = FilamentAdjustment.SourceMeasurement.Length,
        LengthInM = lengthInM,
    };

    private static FilamentAdjustmentDto VolumeAdjustment(Guid filamentId, double volumeMl) => new()
    {
        FilamentId = filamentId,
        Source = FilamentAdjustment.SourceMeasurement.Volume,
        VolumeMl = volumeMl,
    };

    private static FilamentAdjustmentDto WeightAdjustment(Guid filamentId, long amountMg) => new()
    {
        FilamentId = filamentId,
        Source = FilamentAdjustment.SourceMeasurement.Weight,
        AmountMg = amountMg,
    };

    private static async Task AssertUnconvertibleAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"Expected 400, got {(int)response.StatusCode}. Body: {body}");
        Assert.Contains(UnconvertibleMessage, body);
    }

    private static AddFilamentDto NewFilament(
        string displayName,
        double? diameterMm = 1.75,
        double density = 1.24,
        string? category = null,
        long? nominalWeightMg = 1_000_000,
        List<FilamentAdjustmentDto>? adjustments = null) => new()
        {
            DisplayName = displayName,
            Brand = "Test Brand",
            MaterialType = "PLA",
            MaterialCategoryNickname = category,
            ColorName = "Red",
            ColorHex = "FF0000",
            DiameterMm = diameterMm,
            InitialNominalWeightMg = nominalWeightMg,
            MaterialDensityGramPerCubicCm = density,
            IsActive = true,
            FilamentAdjustments = adjustments ?? [],
        };

    private async Task<FilamentDetailDto> CreateFilamentAsync(
        string displayName,
        double? diameterMm = 1.75,
        double density = 1.24,
        string? category = null,
        long? nominalWeightMg = 1_000_000)
    {
        var response = await PostAsync(NewFilament(displayName, diameterMm, density, category, nominalWeightMg));
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Expected 201, got {(int)response.StatusCode}. Body: {body}");
        return (await response.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;
    }

    private async Task<HttpResponseMessage> PostAsync(AddFilamentDto dto)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(dto);
        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// PUTs the filament as the web app does: the whole filament, with every adjustment it
    /// should keep. Optional overrides change the conversion inputs in the same request.
    /// </summary>
    private async Task<HttpResponseMessage> PutAsync(
        FilamentDetailDto filament,
        List<FilamentAdjustmentDto>? adjustments,
        double? density = null,
        double? diameterMm = null,
        bool clearDiameter = false)
    {
        var dto = new FilamentDetailDto
        {
            Id = filament.Id,
            DisplayName = filament.DisplayName,
            Brand = filament.Brand,
            MaterialType = filament.MaterialType,
            MaterialCategoryNickname = filament.MaterialCategoryNickname,
            ColorName = filament.ColorName,
            ColorHex = filament.ColorHex,
            DiameterMm = clearDiameter ? null : diameterMm ?? filament.DiameterMm,
            InitialNominalWeightMg = filament.InitialNominalWeightMg,
            MaterialDensityGramPerCubicCm = density ?? filament.MaterialDensityGramPerCubicCm,
            IsActive = filament.IsActive,
            FilamentAdjustments = adjustments,
        };
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/Filaments/{filament.Id}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(dto);
        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<FilamentDetailDto> GetAsync(Guid id)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Filaments/{id}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;
    }
}
