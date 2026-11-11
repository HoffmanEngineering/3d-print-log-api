using System.Net;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Mcp;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Services;
using Xunit;
using static PrintLogApi.Models.Print;
using static PrintLogApi.Services.MeasurementUtilities;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// A usage row recorded by length or volume stores the weight derived from it, and remaining
/// figures sum that stored weight (FilamentProfile.UsageRowActualMg prefers AmountMg). When the
/// spool's density or diameter is corrected, the adjustments were re-derived (#232, #248) but the
/// usage rows kept the weight of the old density, so remaining went wrong. Both the web PUT and
/// MCP update_material now re-derive them; a row recorded by weight keeps its AmountMg.
/// </summary>
public class FilamentUsageRederivationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _httpClient;

    public FilamentUsageRederivationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _httpClient = factory.CreateClient();
    }

    #region Web PUT

    [Fact]
    public async Task UpdateFilament_DensityChange_LengthUsageKeepsItsLength()
    {
        // 330 m of 1.75 mm at 1.24 g/cm3, 100 m printed. Correcting the density to 1.0 changes what
        // the spool and the print weigh, not how many meters are left on it: still 230 m.
        var spool = await CreateLengthSpoolAsync("Usage Rederive Density Web", lengthM: 330, diameterMm: 1.75, density: 1.24);
        await CreatePrintAsync(spool.Id, new PrintFilamentSummaryDto
        {
            Source = PrintFilament.SourceMeasurement.Length,
            LengthInM = 100,
        });

        await AssertSuccessAsync(await PutAsync(spool, density: 1.0));

        var row = Assert.Single(await UsageRowsAsync(spool.Id));
        Assert.Equal((int?)GetAmountMgFromLength(100, 1.75, 1.0), row.AmountMg);
        Assert.Equal(100, row.LengthInM);
        var reread = await GetAsync(spool.Id);
        Assert.InRange(reread.FilamentLengthRemainingInM!.Value, 229.99, 230.01);
    }

    [Fact]
    public async Task UpdateFilament_DiameterChange_RederivesLengthUsage()
    {
        var spool = await CreateLengthSpoolAsync("Usage Rederive Diameter Web", lengthM: 330, diameterMm: 2.85, density: 1.24);
        await CreatePrintAsync(spool.Id, new PrintFilamentSummaryDto
        {
            Source = PrintFilament.SourceMeasurement.Length,
            LengthInM = 100,
        });

        await AssertSuccessAsync(await PutAsync(spool, diameterMm: 1.75));

        var row = Assert.Single(await UsageRowsAsync(spool.Id));
        Assert.Equal((int?)GetAmountMgFromLength(100, 1.75, 1.24), row.AmountMg);
        Assert.InRange((await GetAsync(spool.Id)).FilamentLengthRemainingInM!.Value, 229.99, 230.01);
    }

    [Fact]
    public async Task UpdateFilament_DensityChange_RederivesEachSideByItsOwnSource()
    {
        // Actual and estimate each name their own source. A length or volume side is re-derived;
        // a weight side keeps the weight the user entered.
        var spool = await CreateLengthSpoolAsync("Usage Rederive Mixed Web", lengthM: 330, diameterMm: 1.75, density: 1.24);
        await CreatePrintAsync(spool.Id, new PrintFilamentSummaryDto
        {
            Source = PrintFilament.SourceMeasurement.Length,
            LengthInM = 10,
            EstimatedSource = PrintFilament.SourceMeasurement.Volume,
            EstimatedVolumeMl = 20,
        });
        await CreatePrintAsync(spool.Id, new PrintFilamentSummaryDto
        {
            Source = PrintFilament.SourceMeasurement.Weight,
            AmountMg = 50_000,
            EstimatedSource = PrintFilament.SourceMeasurement.Length,
            EstimatedLengthInM = 30,
        });

        await AssertSuccessAsync(await PutAsync(spool, density: 2.48));

        var rows = await UsageRowsAsync(spool.Id);
        var byLength = Assert.Single(rows, r => r.Source == PrintFilament.SourceMeasurement.Length);
        Assert.Equal((int?)GetAmountMgFromLength(10, 1.75, 2.48), byLength.AmountMg);
        Assert.Equal(49_600, byLength.EstimatedAmountMg); // 20 ml x 2.48 g/cm3
        var byWeight = Assert.Single(rows, r => r.Source == PrintFilament.SourceMeasurement.Weight);
        Assert.Equal(50_000, byWeight.AmountMg);
        Assert.Equal((int?)GetAmountMgFromLength(30, 1.75, 2.48), byWeight.EstimatedAmountMg);
        // The weight row's derived length follows the new density too.
        Assert.Equal(GetLengthInMetersFromAmount(50_000, 1.75, 2.48), byWeight.LengthInM!.Value, 6);
    }

    [Fact]
    public async Task UpdateFilament_DensityChange_InvalidatesTheOwnersCache()
    {
        // Summaries, analytics and the grouped feed all read usage rows through the version key.
        var spool = await CreateLengthSpoolAsync("Usage Rederive Cache Web", lengthM: 330, diameterMm: 1.75, density: 1.24);
        await CreatePrintAsync(spool.Id, new PrintFilamentSummaryDto
        {
            Source = PrintFilament.SourceMeasurement.Length,
            LengthInM = 100,
        });
        var cache = _factory.Services.GetRequiredService<ICacheVersionService>();
        var before = cache.GetUserCacheVersion(IntegrationTestSeeder.TestUserId);

        await AssertSuccessAsync(await PutAsync(spool, density: 1.0));

        Assert.NotEqual(before, cache.GetUserCacheVersion(IntegrationTestSeeder.TestUserId));
    }

    #endregion

    #region MCP update_material

    [Fact]
    public async Task UpdateMaterialForMcp_DensityChange_LengthUsageKeepsItsLength()
    {
        var spool = await CreateLengthSpoolAsync("Usage Rederive Density Mcp", lengthM: 330, diameterMm: 1.75, density: 1.24);
        await CreatePrintAsync(spool.Id, new PrintFilamentSummaryDto
        {
            Source = PrintFilament.SourceMeasurement.Length,
            LengthInM = 100,
        });

        await UpdateForMcpAsync(spool.Id, new MaterialAttributesInput { DensityGramPerCubicCm = 1.0 });

        var row = Assert.Single(await UsageRowsAsync(spool.Id));
        Assert.Equal((int?)GetAmountMgFromLength(100, 1.75, 1.0), row.AmountMg);
        Assert.InRange((await GetAsync(spool.Id)).FilamentLengthRemainingInM!.Value, 229.99, 230.01);
    }

    [Fact]
    public async Task UpdateMaterialForMcp_DiameterChange_RederivesUsageButKeepsWeightRows()
    {
        var spool = await CreateLengthSpoolAsync("Usage Rederive Diameter Mcp", lengthM: 330, diameterMm: 2.85, density: 1.24);
        await CreatePrintAsync(spool.Id, new PrintFilamentSummaryDto
        {
            Source = PrintFilament.SourceMeasurement.Length,
            LengthInM = 100,
        });
        await CreatePrintAsync(spool.Id, new PrintFilamentSummaryDto
        {
            Source = PrintFilament.SourceMeasurement.Weight,
            AmountMg = 50_000,
        });

        await UpdateForMcpAsync(spool.Id, new MaterialAttributesInput { DiameterMm = 1.75 });

        var rows = await UsageRowsAsync(spool.Id);
        Assert.Equal((int?)GetAmountMgFromLength(100, 1.75, 1.24),
            Assert.Single(rows, r => r.Source == PrintFilament.SourceMeasurement.Length).AmountMg);
        Assert.Equal(50_000, Assert.Single(rows, r => r.Source == PrintFilament.SourceMeasurement.Weight).AmountMg);
    }

    #endregion

    private async Task UpdateForMcpAsync(Guid id, MaterialAttributesInput input)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IFilamentService>().UpdateOwnMaterialForMcp(
            IntegrationTestSeeder.TestUserId, id, input, new HashSet<string>(), TestContext.Current.CancellationToken);
    }

    private async Task<List<PrintFilament>> UsageRowsAsync(Guid filamentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.PrintFilament.AsNoTracking()
            .Where(pf => pf.FilamentId == filamentId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private static async Task AssertSuccessAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"Expected success, got {(int)response.StatusCode}. Body: {body}");
    }

    private async Task<FilamentDetailDto> CreateLengthSpoolAsync(string displayName, double lengthM, double diameterMm, double density)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(new AddFilamentDto
        {
            DisplayName = displayName,
            Brand = "Test Brand",
            MaterialType = "PLA",
            ColorName = "Red",
            ColorHex = "FF0000",
            DiameterMm = diameterMm,
            Source = Filament.SourceMeasurement.Length,
            InitialNominalLengthM = lengthM,
            MaterialDensityGramPerCubicCm = density,
            IsActive = true,
            FilamentAdjustments = [],
        });
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Expected 201, got {(int)response.StatusCode}. Body: {body}");
        return (await response.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;
    }

    private async Task CreatePrintAsync(Guid filamentId, PrintFilamentSummaryDto usage)
    {
        usage.Id = Guid.NewGuid();
        usage.Filament = new FilamentSummaryDto { Id = filamentId };
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(new AddPrintDTO
        {
            Title = "Usage Rederive Print",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Pending,
            ViewStatus = PrintViewStatus.Private,
            FilamentUsage = [usage],
        });
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Expected 201, got {(int)response.StatusCode}. Body: {body}");
    }

    /// <summary>The whole spool as the web app PUTs it, adjustments left out (#248).</summary>
    private async Task<HttpResponseMessage> PutAsync(FilamentDetailDto spool, double? density = null, double? diameterMm = null)
    {
        var dto = new FilamentDetailDto
        {
            Id = spool.Id,
            DisplayName = spool.DisplayName,
            Brand = spool.Brand,
            MaterialType = spool.MaterialType,
            MaterialCategoryNickname = spool.MaterialCategoryNickname,
            ColorName = spool.ColorName,
            ColorHex = spool.ColorHex,
            DiameterMm = diameterMm ?? spool.DiameterMm,
            Source = spool.Source,
            InitialNominalLengthM = spool.InitialNominalLengthM,
            InitialNominalWeightMg = spool.InitialNominalWeightMg,
            MaterialDensityGramPerCubicCm = density ?? spool.MaterialDensityGramPerCubicCm,
            IsActive = spool.IsActive,
            FilamentAdjustments = null,
        };
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/Filaments/{spool.Id}");
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
