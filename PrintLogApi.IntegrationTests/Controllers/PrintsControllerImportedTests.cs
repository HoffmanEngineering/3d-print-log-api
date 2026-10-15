using System.Net;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Printer;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Imported prints (printlog-bridge#8): a connector backfilling its printer's history creates
/// prints that already happened. They are records only: creating one must not change what is
/// loaded on the printer or any spool's remaining weight, and must never change a print the
/// connector already logged live under the same external pair.
/// </summary>
public class PrintsControllerImportedTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _httpClient;

    public PrintsControllerImportedTests(CustomWebApplicationFactory factory)
    {
        _httpClient = factory.CreateClient();
    }

    [Fact]
    public async Task PostPrint_Imported_LeavesTheSingleToolPrintersLoadedSpool()
    {
        // A print's spools normally become a one-slot printer's loaded list, so a print with no
        // spool would unload the spool that is in the printer today.
        var printerId = await CreatePrinterAsync();
        var spool = await CreateFilamentAsync();
        await LoadSlotAsync(printerId, 0, spool.Id);

        var response = await PostAsync(ImportedPrint(printerId, UniqueExternalId()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var loaded = Assert.Single(await GetLoadedAsync(printerId));
        Assert.Equal(spool.Id, loaded.Filament?.Id);
        Assert.Equal(0, loaded.Slot);
    }

    [Fact]
    public async Task PostPrint_Imported_RecordsItsUsageWithoutTouchingAnySpool()
    {
        var printerId = await CreatePrinterAsync();
        var spool = await CreateFilamentAsync();
        await LoadSlotAsync(printerId, 0, spool.Id);

        var created = await ReadAsync(await PostAsync(ImportedPrint(printerId, UniqueExternalId())));

        Assert.Equal(PrintStatus.Success, created.Status);
        var row = Assert.Single(created.FilamentUsage!);
        Assert.Null(row.Filament);
        Assert.Equal(12.5, row.LengthInM);
        Assert.Equal(1_000_000, await GetRemainingAsync(spool.Id));
    }

    [Fact]
    public async Task PostPrint_Imported_UsageRowNamingASpool_Returns400()
    {
        // Historical usage would be counted against spools bought after it was printed.
        var printerId = await CreatePrinterAsync();
        var spool = await CreateFilamentAsync();
        var dto = ImportedPrint(printerId, UniqueExternalId());
        dto.FilamentUsage!.Single().Filament = new FilamentSummaryDto { Id = spool.Id };

        var response = await PostAsync(dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1_000_000, await GetRemainingAsync(spool.Id));
    }

    [Theory]
    [InlineData(PrintStatus.Pending)]
    [InlineData(PrintStatus.Printing)]
    public async Task PostPrint_Imported_NotFinished_Returns400(PrintStatus status)
    {
        // Nothing would ever finish it: the connector logs the jobs it sees live itself.
        var dto = ImportedPrint(IntegrationTestSeeder.TestPrinterId, UniqueExternalId());
        dto.Status = status;

        var response = await PostAsync(dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostPrint_Imported_ReplayOntoAPrintLoggedLive_ChangesNothing()
    {
        // The live connector created and completed the print with its spool; a backfill that
        // reaches the same job must neither unlink the spool nor rewrite the print.
        var externalId = UniqueExternalId();
        var printerId = await CreatePrinterAsync();
        var spool = await CreateFilamentAsync();
        var live = new AddPrintDTO
        {
            Title = "benchy.gcode",
            PrinterId = printerId,
            Status = PrintStatus.Printing,
            ViewStatus = PrintViewStatus.Private,
            ExternalSource = "moonraker",
            ExternalId = externalId,
        };
        var created = await ReadAsync(await PostAsync(live));
        await ReadAsync(await CompleteAsync(created.Id, new CompletePrintDto
        {
            Status = PrintStatus.PartialSuccess,
            PrintTimeInSeconds = 3600,
            FilamentUsage = [new CompletePrintFilamentUsageDto { Slot = 0, FilamentId = spool.Id, AmountMg = 22_000 }],
        }));

        var backfill = ImportedPrint(printerId, externalId);
        backfill.PrintTimeInSeconds = 1;
        var replay = await PostAsync(backfill);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(created.Id, (await ReadAsync(replay)).Id);
        var detail = await GetDetailAsync(created.Id);
        Assert.Equal(PrintStatus.PartialSuccess, detail.Status);
        Assert.Equal(3600, detail.PrintTimeInSeconds);
        Assert.Null(detail.Notes);
        var row = Assert.Single(detail.FilamentUsage!);
        Assert.Equal(spool.Id, row.Filament?.Id);
        Assert.Equal(22_000, row.AmountMg);
        Assert.Equal(1_000_000 - 22_000, await GetRemainingAsync(spool.Id));
    }

    [Fact]
    public async Task PostPrint_ImportedTwice_SecondReturnsOkWithTheSamePrint()
    {
        var externalId = UniqueExternalId();

        var first = await PostAsync(ImportedPrint(IntegrationTestSeeder.TestPrinterId, externalId));
        var second = await PostAsync(ImportedPrint(IntegrationTestSeeder.TestPrinterId, externalId));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal((await ReadAsync(first)).Id, (await ReadAsync(second)).Id);
    }

    [Fact]
    public async Task PostPrint_ImportedWithoutAnExternalPair_Returns400NamingImported()
    {
        // Without a pair, a backfill run twice would import everything twice.
        var dto = ImportedPrint(IntegrationTestSeeder.TestPrinterId, "unused");
        dto.ExternalSource = null;
        dto.ExternalId = null;

        var response = await PostAsync(dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"Imported\"", body);
    }

    [Fact]
    public async Task PostPrint_Imported_ReplayOntoAPrintStillPrinting_LeavesItPrintingAndUnchanged()
    {
        // The live connector is still logging that job; it finishes the print itself.
        var externalId = UniqueExternalId();
        var printerId = await CreatePrinterAsync();
        var live = new AddPrintDTO
        {
            Title = "benchy.gcode",
            PrinterId = printerId,
            Status = PrintStatus.Printing,
            ViewStatus = PrintViewStatus.Private,
            EstimatedPrintTimeInSeconds = 3500,
            ExternalSource = "moonraker",
            ExternalId = externalId,
        };
        var created = await ReadAsync(await PostAsync(live));

        var replay = await PostAsync(ImportedPrint(printerId, externalId));

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var detail = await GetDetailAsync(created.Id);
        Assert.Equal(PrintStatus.Printing, detail.Status);
        Assert.Null(detail.PrintTimeInSeconds);
        Assert.Equal(3500, detail.EstimatedPrintTimeInSeconds);
        Assert.Empty(detail.FilamentUsage!);
        Assert.Null(detail.Notes);
    }

    /// <summary>A finished job from a printer's history, its total usage on one unlinked row.</summary>
    private static AddPrintDTO ImportedPrint(long printerId, string externalId) => new()
    {
        Title = "benchy.gcode",
        PrinterId = printerId,
        Status = PrintStatus.Success,
        ViewStatus = PrintViewStatus.Private,
        StartDate = DateTimeOffset.UtcNow.AddDays(-30),
        PrintTimeInSeconds = 3000,
        Notes = "Imported from Moonraker history",
        ExternalSource = "moonraker",
        ExternalId = externalId,
        Imported = true,
        FilamentUsage =
        [
            new PrintFilamentSummaryDto { Id = Guid.NewGuid(), LengthInM = 12.5, Source = PrintFilament.SourceMeasurement.Length },
        ],
    };

    private static string UniqueExternalId() => $"{Guid.NewGuid()}:{Random.Shared.Next(1, 100000)}";

    private async Task<HttpResponseMessage> PostAsync(AddPrintDTO dto)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(dto);
        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> CompleteAsync(long id, CompletePrintDto body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/Prints/{id}/complete");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(body);
        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task LoadSlotAsync(long printerId, int slot, Guid filamentId)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/Printers/{printerId}/slots/{slot}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(new LoadPrinterSlotDto { FilamentId = filamentId });
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private async Task<List<PrinterFilamentSummaryDto>> GetLoadedAsync(long printerId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Printers/{printerId}/filament");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<PrinterFilamentSummaryDto>>(TestContext.Current.CancellationToken))!;
    }

    /// <summary>A fresh 1 kg spool, so remaining-weight assertions see only this test's prints.</summary>
    private async Task<FilamentDetailDto> CreateFilamentAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(new AddFilamentDto
        {
            DisplayName = $"Imported print test {Guid.NewGuid():N}",
            Brand = "Test Brand",
            MaterialType = "PLA",
            ColorName = "Green",
            ColorHex = "00FF00",
            DiameterMm = 1.75,
            InitialNominalWeightMg = 1_000_000,
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true,
        });
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<FilamentDetailDto>(TestContext.Current.CancellationToken))!;
    }

    private async Task<long?> GetRemainingAsync(Guid filamentId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Filaments/{filamentId}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<FilamentDetailDto>(TestContext.Current.CancellationToken))!.FilamentRemaining;
    }

    private async Task<PrintDetailDTO> ReadAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<PrintDetailDTO>(TestContext.Current.CancellationToken))!;
    }

    private async Task<PrintDetailDTO> GetDetailAsync(long id)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Prints/{id}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        return await ReadAsync(await _httpClient.SendAsync(request, TestContext.Current.CancellationToken));
    }

    private async Task<long> CreatePrinterAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Printers");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(new AddPrinterDTO { Name = "Imported print printer", Make = "Make", Model = "Model", IsActive = true });
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<PrinterDetailDto>(TestContext.Current.CancellationToken))!.Id!.Value;
    }
}
