using System.Net;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Printer;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Resin usage in milliliters on a connector's print (printlog-bridge#97): a resin printer reports
/// volume per slot, which is charged to the bottle as a volume, never through a length. The
/// bodies are written as the bridge sends them, so these pin the wire contract.
/// </summary>
public class PrintsControllerResinVolumeTests : IClassFixture<CustomWebApplicationFactory>
{
    private const double Density = 1.1;

    private readonly HttpClient _httpClient;

    public PrintsControllerResinVolumeTests(CustomWebApplicationFactory factory)
    {
        _httpClient = factory.CreateClient();
    }

    [Fact]
    public async Task Complete_VolumeOnABottle_RecordsMilliliters_AndChargesTheBottleByVolume()
    {
        var printerId = await CreateResinPrinterAsync();
        var bottle = await CreateBottleAsync();
        var created = await CreatePrintAsync(printerId);

        var completed = await ReadAsync(await CompleteJsonAsync(created.Id, $$"""
            {"status": 3, "filamentUsage": [
              {"slot": 0, "filamentId": "{{bottle.Id}}", "volumeMl": 42.5, "source": 3, "actualIsEstimate": true}
            ]}
            """));

        var row = Assert.Single(completed.FilamentUsage!);
        Assert.Equal(bottle.Id, row.Filament?.Id);
        Assert.Equal(0, row.Slot);
        Assert.Equal(PrintFilament.SourceMeasurement.Volume, row.Source);
        Assert.Equal(42.5, row.VolumeMl);
        Assert.Null(row.LengthInM); // resin has no diameter: no length is made up
        Assert.Equal(46_750, row.AmountMg); // 42.5 mL at 1.1 g/mL
        Assert.True(row.ActualIsEstimate);
        var after = await GetFilamentAsync(bottle.Id);
        Assert.Equal(957.5, after.FilamentVolumeRemainingInMl!.Value, 2);
    }

    [Fact]
    public async Task Complete_VolumeOnABottle_KeepsTheVolumeEstimateTheStartSent()
    {
        var printerId = await CreateResinPrinterAsync();
        var bottle = await CreateBottleAsync();
        var created = await CreatePrintJsonAsync(printerId, $$"""
            [{"slot": 0, "filament": {"id": "{{bottle.Id}}"}, "estimatedVolumeMl": 40.0, "estimatedSource": 3}]
            """);
        var started = Assert.Single(created.FilamentUsage!);
        Assert.Equal(40.0, started.EstimatedVolumeMl);
        Assert.Equal(PrintFilament.SourceMeasurement.Volume, started.EstimatedSource);

        var completed = await ReadAsync(await CompleteJsonAsync(created.Id, $$"""
            {"status": 3, "filamentUsage": [{"slot": 0, "filamentId": "{{bottle.Id}}", "volumeMl": 42.5, "source": 3}]}
            """));

        var row = Assert.Single(completed.FilamentUsage!);
        Assert.Equal(40.0, row.EstimatedVolumeMl);
        Assert.Equal(42.5, row.VolumeMl);
        Assert.Equal(957.5, (await GetFilamentAsync(bottle.Id)).FilamentVolumeRemainingInMl!.Value, 2);
    }

    [Fact]
    public async Task Complete_VolumeAndWeightWithNoSource_OnABottle_UsesTheBottlesVolume()
    {
        var printerId = await CreateResinPrinterAsync();
        var bottle = await CreateBottleAsync();
        var created = await CreatePrintAsync(printerId);

        // Without a source the default order would take the weight first; the bottle is
        // measured in volume and the row carries one, so that is the measurement.
        var completed = await ReadAsync(await CompleteJsonAsync(created.Id, $$"""
            {"status": 3, "filamentUsage": [{"slot": 0, "filamentId": "{{bottle.Id}}", "amountMg": 50000, "volumeMl": 42.5}]}
            """));

        var row = Assert.Single(completed.FilamentUsage!);
        Assert.Equal(PrintFilament.SourceMeasurement.Volume, row.Source);
        Assert.Equal(42.5, row.VolumeMl);
        Assert.Equal(46_750, row.AmountMg);
        Assert.Equal(957.5, (await GetFilamentAsync(bottle.Id)).FilamentVolumeRemainingInMl!.Value, 2);
    }

    [Fact]
    public async Task Complete_LengthSourceWithAVolume_OnABottle_ChargesTheVolume()
    {
        var printerId = await CreateResinPrinterAsync();
        var bottle = await CreateBottleAsync();
        var created = await CreatePrintAsync(printerId);

        // A length cannot be converted on a material with no diameter, so the volume the row
        // also carries is the measurement.
        var completed = await ReadAsync(await CompleteJsonAsync(created.Id, $$"""
            {"status": 3, "filamentUsage": [{"slot": 0, "filamentId": "{{bottle.Id}}", "lengthInM": 3.0, "volumeMl": 42.5, "source": 2}]}
            """));

        var row = Assert.Single(completed.FilamentUsage!);
        Assert.Equal(PrintFilament.SourceMeasurement.Volume, row.Source);
        Assert.Equal(42.5, row.VolumeMl);
        Assert.Equal(46_750, row.AmountMg);
        Assert.Equal(957.5, (await GetFilamentAsync(bottle.Id)).FilamentVolumeRemainingInMl!.Value, 2);
    }

    [Fact]
    public async Task Complete_OnlyALength_OnABottle_RecordsTheLength_AndLeavesTheBottleAlone()
    {
        var printerId = await CreateResinPrinterAsync();
        var bottle = await CreateBottleAsync();
        var created = await CreatePrintAsync(printerId);

        var completed = await ReadAsync(await CompleteJsonAsync(created.Id, $$"""
            {"status": 3, "filamentUsage": [{"slot": 0, "filamentId": "{{bottle.Id}}", "lengthInM": 3.0, "source": 2}]}
            """));

        var row = Assert.Single(completed.FilamentUsage!);
        Assert.Equal(bottle.Id, row.Filament?.Id);
        Assert.Equal(PrintFilament.SourceMeasurement.Length, row.Source);
        Assert.Equal(3.0, row.LengthInM);
        Assert.Null(row.AmountMg);
        Assert.Null(row.VolumeMl);
        var after = await GetFilamentAsync(bottle.Id);
        Assert.Equal(1000.0, after.FilamentVolumeRemainingInMl!.Value, 2);
    }

    [Fact]
    public async Task Complete_OnlyAVolume_OnAFilamentSpool_ChargesItsWeightByDensity()
    {
        var spool = await CreatePlaSpoolAsync();
        var created = await CreatePrintAsync(IntegrationTestSeeder.TestPrinterId);

        var completed = await ReadAsync(await CompleteJsonAsync(created.Id, $$"""
            {"status": 3, "filamentUsage": [{"slot": 0, "filamentId": "{{spool.Id}}", "volumeMl": 10.0, "source": 3}]}
            """));

        // Volume to weight needs only the density, so it is exact on any material.
        var row = Assert.Single(completed.FilamentUsage!);
        Assert.Equal(PrintFilament.SourceMeasurement.Volume, row.Source);
        Assert.Equal(10.0, row.VolumeMl);
        Assert.Equal(12_400, row.AmountMg); // 10 mL at 1.24 g/mL
        Assert.InRange(row.LengthInM!.Value, 4.1, 4.2); // 10 cm3 over a 1.75 mm cross-section
        Assert.Equal(1_000_000 - 12_400, (await GetFilamentAsync(spool.Id)).FilamentRemaining);
    }

    [Fact]
    public async Task Complete_WeightAndVolumeWithNoSource_OnAWeightSpool_StillUsesTheWeight()
    {
        var spool = await CreatePlaSpoolAsync();
        var created = await CreatePrintAsync(IntegrationTestSeeder.TestPrinterId);

        var completed = await ReadAsync(await CompleteJsonAsync(created.Id, $$"""
            {"status": 3, "filamentUsage": [{"filamentId": "{{spool.Id}}", "amountMg": 20000, "volumeMl": 10.0}]}
            """));

        var row = Assert.Single(completed.FilamentUsage!);
        Assert.Equal(PrintFilament.SourceMeasurement.Weight, row.Source);
        Assert.Equal(20_000, row.AmountMg);
        Assert.Equal(1_000_000 - 20_000, (await GetFilamentAsync(spool.Id)).FilamentRemaining);
    }

    [Fact]
    public async Task StartAndFinish_OnAResinPrinter_WithNoUsageAtAll_Works()
    {
        var printerId = await CreateResinPrinterAsync();
        var created = await CreatePrintAsync(printerId);
        Assert.Empty(created.FilamentUsage!);

        var completed = await ReadAsync(await CompleteJsonAsync(created.Id, """{"status": 3, "printTimeInSeconds": 5400}"""));

        Assert.Equal(PrintStatus.Success, completed.Status);
        Assert.Equal(5400, completed.PrintTimeInSeconds);
        Assert.Empty(completed.FilamentUsage!);
    }

    [Fact]
    public async Task Complete_OnAResinPrinter_WithNoBottleLoaded_KeepsTheVolumeOnTheSlot()
    {
        var printerId = await CreateResinPrinterAsync();
        var created = await CreatePrintAsync(printerId);

        var completed = await ReadAsync(await CompleteJsonAsync(created.Id, """
            {"status": 3, "filamentUsage": [{"slot": 0, "volumeMl": 42.5, "source": 3, "actualIsEstimate": true}]}
            """));

        var row = Assert.Single(completed.FilamentUsage!);
        Assert.Null(row.Filament);
        Assert.Equal(0, row.Slot);
        Assert.Equal(PrintFilament.SourceMeasurement.Volume, row.Source);
        Assert.Equal(42.5, row.VolumeMl);
        Assert.True(row.ActualIsEstimate);

        // The same body again matches that row by slot rather than adding a second one.
        var again = await ReadAsync(await CompleteJsonAsync(created.Id, """
            {"status": 3, "filamentUsage": [{"slot": 0, "volumeMl": 42.5, "source": 3, "actualIsEstimate": true}]}
            """));
        Assert.Single(again.FilamentUsage!);
    }

    [Fact]
    public async Task Complete_VolumeOnABottle_Twice_ChargesTheBottleOnce()
    {
        var printerId = await CreateResinPrinterAsync();
        var bottle = await CreateBottleAsync();
        var created = await CreatePrintAsync(printerId);
        var body = $$"""
            {"status": 3, "filamentUsage": [{"slot": 0, "filamentId": "{{bottle.Id}}", "volumeMl": 42.5, "source": 3}]}
            """;

        await ReadAsync(await CompleteJsonAsync(created.Id, body));
        await ReadAsync(await CompleteJsonAsync(created.Id, body));

        Assert.Equal(957.5, (await GetFilamentAsync(bottle.Id)).FilamentVolumeRemainingInMl!.Value, 2);
    }

    private async Task<long> CreateResinPrinterAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Printers");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(new AddPrinterDTO
        {
            Name = $"Resin {Guid.NewGuid():N}"[..20],
            Make = "Elegoo",
            Model = "Saturn 4",
            Category = "MSLA",
            IsActive = true,
        });
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<PrinterDetailDto>(TestContext.Current.CancellationToken))!.Id!.Value;
    }

    /// <summary>A fresh 1 L bottle of resin per test, measured in volume, with no diameter.</summary>
    private Task<FilamentDetailDto> CreateBottleAsync() => CreateFilamentAsync(new AddFilamentDto
    {
        DisplayName = $"Resin bottle {Guid.NewGuid():N}",
        Brand = "Elegoo",
        MaterialType = "Standard Resin",
        MaterialCategoryNickname = "resin",
        ColorName = "Grey",
        ColorHex = "808080",
        Source = Filament.SourceMeasurement.Volume,
        InitialNominalVolumeMl = 1000,
        MaterialDensityGramPerCubicCm = Density,
        IsActive = true,
    });

    /// <summary>A fresh 1 kg spool of 1.75 mm PLA per test, measured in weight.</summary>
    private Task<FilamentDetailDto> CreatePlaSpoolAsync() => CreateFilamentAsync(new AddFilamentDto
    {
        DisplayName = $"PLA spool {Guid.NewGuid():N}",
        Brand = "Test Brand",
        MaterialType = "PLA",
        ColorName = "Green",
        ColorHex = "00FF00",
        DiameterMm = 1.75,
        InitialNominalWeightMg = 1_000_000,
        MaterialDensityGramPerCubicCm = 1.24,
        IsActive = true,
    });

    private async Task<FilamentDetailDto> CreateFilamentAsync(AddFilamentDto body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(body);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<FilamentDetailDto>(TestContext.Current.CancellationToken))!;
    }

    private async Task<FilamentDetailDto> GetFilamentAsync(Guid id)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Filaments/{id}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<FilamentDetailDto>(TestContext.Current.CancellationToken))!;
    }

    /// <summary>A Printing print with no usage, as the bridge starts a job the file says nothing about.</summary>
    private Task<PrintDetailDTO> CreatePrintAsync(long printerId) => CreatePrintJsonAsync(printerId, "[]");

    private async Task<PrintDetailDTO> CreatePrintJsonAsync(long printerId, string filamentUsageJson)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = new StringContent($$"""
            {"printerId": {{printerId}}, "title": "resin {{Guid.NewGuid():N}}", "fileName": "part.ctb",
             "startDate": "{{DateTimeOffset.UtcNow:O}}", "status": 2, "viewStatus": 0,
             "externalSource": "printlog-bridge", "externalId": "test:{{Guid.NewGuid():N}}",
             "filamentUsage": {{filamentUsageJson}}}
            """, System.Text.Encoding.UTF8, "application/json");
        return await ReadAsync(await _httpClient.SendAsync(request, TestContext.Current.CancellationToken));
    }

    private async Task<HttpResponseMessage> CompleteJsonAsync(long id, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/Prints/{id}/complete");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<PrintDetailDTO> ReadAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        return (await response.Content.ReadFromJsonAsync<PrintDetailDTO>(TestContext.Current.CancellationToken))!;
    }
}
