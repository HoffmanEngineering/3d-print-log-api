using System.Net;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Printer;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// PATCH and complete (#145): a connector finishing a print must not overwrite what the user
/// edited while it ran, and must not deduct inventory twice when it retries.
/// </summary>
public class PrintsControllerPatchCompleteTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _httpClient;

    public PrintsControllerPatchCompleteTests(CustomWebApplicationFactory factory)
    {
        _httpClient = factory.CreateClient();
    }

    #region PATCH

    [Fact]
    public async Task Patch_OnlyTitle_LeavesEveryOtherFieldAlone()
    {
        var created = await CreatePrintAsync(notes: "original notes", status: PrintStatus.Printing);

        var response = await PatchAsync(created.Id, new { title = "Renamed" });

        var patched = await ReadAsync(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Renamed", patched.Title);
        Assert.Equal("original notes", patched.Notes);
        Assert.Equal(PrintStatus.Printing, patched.Status);
        Assert.Equal(created.StartDate, patched.StartDate);
    }

    [Fact]
    public async Task Patch_SetsEachSuppliedField()
    {
        var created = await CreatePrintAsync();
        var start = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        var patched = await ReadAsync(await PatchAsync(created.Id, new
        {
            status = PrintStatus.Failed,
            startDate = start,
            estimatedPrintTimeInSeconds = 100,
            printTimeInSeconds = 90,
            notes = "new notes",
            url = "https://example.com/model",
            fileName = "model.gcode",
            allowComments = true,
            allowFileDownloads = true,
            viewStatus = PrintViewStatus.Unlisted,
        }));

        Assert.Equal(PrintStatus.Failed, patched.Status);
        Assert.Equal(start, patched.StartDate);
        Assert.Equal(100, patched.EstimatedPrintTimeInSeconds);
        Assert.Equal(90, patched.PrintTimeInSeconds);
        Assert.Equal("new notes", patched.Notes);
        Assert.Equal("https://example.com/model", patched.Url);
        Assert.Equal("model.gcode", patched.FileName);
        Assert.True(patched.AllowComments);
        Assert.True(patched.AllowFileDownloads);
        Assert.Equal(PrintViewStatus.Unlisted, patched.ViewStatus);
    }

    [Fact]
    public async Task Patch_Clear_NullsTheNamedFields()
    {
        var created = await CreatePrintAsync(notes: "to be cleared", printTimeInSeconds: 60, startDate: DateTimeOffset.UtcNow);

        var patched = await ReadAsync(await PatchAsync(created.Id, new { clear = new[] { "notes", "printTimeInSeconds", "startDate" } }));

        Assert.Null(patched.Notes);
        Assert.Null(patched.PrintTimeInSeconds);
        Assert.Null(patched.StartDate);
        Assert.Equal(created.Title, patched.Title);
    }

    [Fact]
    public async Task Patch_SetAndClearTheSameField_IsBadRequestAndChangesNothing()
    {
        var created = await CreatePrintAsync(notes: "kept");

        var response = await PatchAsync(created.Id, new { title = "Not applied", notes = "x", clear = new[] { "notes" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var detail = await GetDetailAsync(created.Id);
        Assert.Equal("kept", detail.Notes);
        Assert.Equal(created.Title, detail.Title);
    }

    [Fact]
    public async Task Patch_ClearAnUnknownOrRequiredField_IsBadRequest()
    {
        var created = await CreatePrintAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await PatchAsync(created.Id, new { clear = new[] { "nope" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PatchAsync(created.Id, new { clear = new[] { "title" } })).StatusCode);
    }

    [Fact]
    public async Task Patch_BlankTitle_IsBadRequest()
    {
        var created = await CreatePrintAsync();

        var response = await PatchAsync(created.Id, new { title = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Patch_MissingPrint_IsNotFound()
    {
        var response = await PatchAsync(long.MaxValue, new { title = "x" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Patch_AnotherUsersPrint_IsForbidden()
    {
        var created = await CreatePrintAsync(viewStatus: PrintViewStatus.Public);

        var response = await PatchAsync(created.Id, new { title = "hijacked" }, IntegrationTestSeeder.SecondaryUserOAuthId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(created.Title, (await GetDetailAsync(created.Id)).Title);
    }

    [Fact]
    public async Task Patch_PrinterTheCallerDoesNotOwn_IsBadRequest()
    {
        var created = await CreatePrintAsync();
        var foreignPrinterId = await CreatePrinterAsync(IntegrationTestSeeder.SecondaryUserOAuthId);

        var response = await PatchAsync(created.Id, new { printerId = foreignPrinterId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Patch_UnknownProject_IsNotFound()
    {
        var created = await CreatePrintAsync();

        var response = await PatchAsync(created.Id, new { projectId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Patch_FilamentUsage_ReplacesTheRows()
    {
        var filament = await CreateFilamentAsync();
        var created = await CreatePrintAsync(usage: [EstimateRow(filament.Id, 20_000)]);

        var patched = await ReadAsync(await PatchAsync(created.Id, new
        {
            filamentUsage = new[] { new { filamentId = filament.Id, amountMg = 12_000, source = PrintFilament.SourceMeasurement.Weight } },
        }));

        var row = Assert.Single(patched.FilamentUsage!);
        Assert.Equal(12_000, row.AmountMg);
        Assert.Null(row.EstimatedAmountMg);
    }

    [Fact]
    public async Task Patch_FilamentTheCallerDoesNotOwn_IsBadRequest()
    {
        var created = await CreatePrintAsync();
        var foreign = await CreateFilamentAsync(IntegrationTestSeeder.SecondaryUserOAuthId);

        var response = await PatchAsync(created.Id, new
        {
            filamentUsage = new[] { new { filamentId = foreign.Id, amountMg = 1_000 } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_StillReplacesTheWholePrint()
    {
        var created = await CreatePrintAsync(notes: "will be replaced");

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/Prints/{created.Id}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(new PutPrintDetailDto
        {
            Id = created.Id,
            Title = "Put title",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Success,
            ViewStatus = PrintViewStatus.Private,
            FilamentUsage = [],
        });
        var put = await ReadAsync(await _httpClient.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal("Put title", put.Title);
        Assert.Null(put.Notes);
    }

    #endregion

    #region complete

    [Fact]
    public async Task Complete_KeepsTheTitleAndNotesTheUserEditedWhileItRan()
    {
        var created = await CreatePrintAsync(status: PrintStatus.Printing);
        await ReadAsync(await PatchAsync(created.Id, new { title = "User title", notes = "User notes" }));

        var completed = await ReadAsync(await CompleteAsync(created.Id, new CompletePrintDto
        {
            Status = PrintStatus.Success,
            PrintTimeInSeconds = 3_600,
        }));

        Assert.Equal("User title", completed.Title);
        Assert.Equal("User notes", completed.Notes);
        Assert.Equal(PrintStatus.Success, completed.Status);
        Assert.Equal(3_600, completed.PrintTimeInSeconds);
    }

    [Fact]
    public async Task Complete_MergesUsageByFilament_KeepingTheEstimateAndNotes()
    {
        var filament = await CreateFilamentAsync();
        var estimate = EstimateRow(filament.Id, 20_000);
        estimate.Notes = "left spool";
        var created = await CreatePrintAsync(status: PrintStatus.Printing, usage: [estimate]);

        var completed = await ReadAsync(await CompleteAsync(created.Id, Completion(filament.Id, 22_000)));

        var row = Assert.Single(completed.FilamentUsage!);
        Assert.Equal(20_000, row.EstimatedAmountMg);
        Assert.Equal(22_000, row.AmountMg);
        Assert.Equal("left spool", row.Notes);
        Assert.Equal(PrintFilament.SourceMeasurement.Weight, row.Source);
    }

    [Fact]
    public async Task Complete_FilamentNotOnThePrint_AddsARow()
    {
        var planned = await CreateFilamentAsync();
        var swapped = await CreateFilamentAsync();
        var created = await CreatePrintAsync(status: PrintStatus.Printing, usage: [EstimateRow(planned.Id, 20_000)]);

        var completed = await ReadAsync(await CompleteAsync(created.Id, Completion(swapped.Id, 18_000)));

        Assert.Equal(2, completed.FilamentUsage!.Count);
        var plannedRow = Assert.Single(completed.FilamentUsage, r => r.Filament?.Id == planned.Id);
        Assert.Null(plannedRow.AmountMg);
        var swappedRow = Assert.Single(completed.FilamentUsage, r => r.Filament?.Id == swapped.Id);
        Assert.Equal(18_000, swappedRow.AmountMg);
        Assert.Null(swappedRow.EstimatedAmountMg);
    }

    [Fact]
    public async Task Complete_LengthSource_DerivesTheWeightFromTheFilament()
    {
        var filament = await CreateFilamentAsync();
        var created = await CreatePrintAsync(status: PrintStatus.Printing);

        var completed = await ReadAsync(await CompleteAsync(created.Id, new CompletePrintDto
        {
            Status = PrintStatus.Success,
            FilamentUsage = [new CompletePrintFilamentUsageDto { FilamentId = filament.Id, LengthInM = 10 }],
        }));

        var row = Assert.Single(completed.FilamentUsage!);
        Assert.Equal(PrintFilament.SourceMeasurement.Length, row.Source);
        Assert.Equal(10, row.LengthInM);
        Assert.InRange(row.AmountMg!.Value, 29_000, 30_500); // 1.75 mm PLA at 1.24 g/cm3 is ~2.98 g/m
    }

    [Fact]
    public async Task Complete_TwiceWithTheSameBody_DeductsInventoryOnce()
    {
        var filament = await CreateFilamentAsync();
        var created = await CreatePrintAsync(status: PrintStatus.Printing, usage: [EstimateRow(filament.Id, 20_000)]);

        await ReadAsync(await CompleteAsync(created.Id, Completion(filament.Id, 20_000)));
        var afterFirst = await GetRemainingAsync(filament.Id);
        var second = await CompleteAsync(created.Id, Completion(filament.Id, 20_000));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1_000_000 - 20_000, afterFirst);
        Assert.Equal(afterFirst, await GetRemainingAsync(filament.Id));
        Assert.Single((await GetDetailAsync(created.Id)).FilamentUsage!);
    }

    [Fact]
    public async Task Complete_AgainWithMoreUsage_DeductsOnlyTheDifference()
    {
        var filament = await CreateFilamentAsync();
        var created = await CreatePrintAsync(status: PrintStatus.Printing);

        await ReadAsync(await CompleteAsync(created.Id, Completion(filament.Id, 20_000)));
        await ReadAsync(await CompleteAsync(created.Id, Completion(filament.Id, 25_000)));

        Assert.Equal(1_000_000 - 25_000, await GetRemainingAsync(filament.Id));
    }

    [Fact]
    public async Task Complete_ThenPatchUsageFrom20To25Grams_DeductsExactly5GramsMore()
    {
        var filament = await CreateFilamentAsync();
        var created = await CreatePrintAsync(status: PrintStatus.Printing);
        await ReadAsync(await CompleteAsync(created.Id, Completion(filament.Id, 20_000)));
        var afterComplete = await GetRemainingAsync(filament.Id);

        await ReadAsync(await PatchAsync(created.Id, new
        {
            filamentUsage = new[] { new { filamentId = filament.Id, amountMg = 25_000, source = PrintFilament.SourceMeasurement.Weight } },
        }));

        Assert.Equal(afterComplete - 5_000, await GetRemainingAsync(filament.Id));
    }

    [Fact]
    public async Task Complete_UnlinkedUsageTwice_DoesNotDuplicateTheRow()
    {
        var created = await CreatePrintAsync(status: PrintStatus.Printing);
        var body = new CompletePrintDto
        {
            Status = PrintStatus.Success,
            FilamentUsage = [new CompletePrintFilamentUsageDto { LengthInM = 4.5 }],
        };

        await ReadAsync(await CompleteAsync(created.Id, body));
        var completed = await ReadAsync(await CompleteAsync(created.Id, body));

        var row = Assert.Single(completed.FilamentUsage!);
        Assert.Null(row.Filament);
        Assert.Equal(4.5, row.LengthInM);
    }

    [Fact]
    public async Task Complete_EndedAtWithoutDuration_DerivesTheDurationFromTheStartDate()
    {
        var start = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var created = await CreatePrintAsync(status: PrintStatus.Printing, startDate: start);

        var completed = await ReadAsync(await CompleteAsync(created.Id, new CompletePrintDto
        {
            Status = PrintStatus.Success,
            EndedAt = start.AddMinutes(90),
        }));

        Assert.Equal(5_400, completed.PrintTimeInSeconds);
        Assert.Equal(start, completed.StartDate);
    }

    [Fact]
    public async Task Complete_EndedAtWithoutStartDate_DerivesTheStartDateFromTheDuration()
    {
        var created = await CreatePrintAsync(status: PrintStatus.Printing, startDate: null);
        var end = new DateTimeOffset(2026, 10, 1, 14, 0, 0, TimeSpan.Zero);

        var completed = await ReadAsync(await CompleteAsync(created.Id, new CompletePrintDto
        {
            Status = PrintStatus.Success,
            EndedAt = end,
            PrintTimeInSeconds = 3_600,
        }));

        Assert.Equal(end.AddHours(-1), completed.StartDate);
    }

    [Fact]
    public async Task Complete_EndedAtBeforeTheStartDate_IsBadRequest()
    {
        var start = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var created = await CreatePrintAsync(status: PrintStatus.Printing, startDate: start);

        var response = await CompleteAsync(created.Id, new CompletePrintDto
        {
            Status = PrintStatus.Success,
            EndedAt = start.AddMinutes(-5),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(PrintStatus.Pending)]
    [InlineData(PrintStatus.Printing)]
    [InlineData((PrintStatus)0)]
    public async Task Complete_WithAStatusThatIsNotFinished_IsBadRequest(PrintStatus status)
    {
        var created = await CreatePrintAsync(status: PrintStatus.Printing);

        var response = await CompleteAsync(created.Id, new CompletePrintDto { Status = status });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Complete_TheSameFilamentTwiceInOneBody_IsBadRequest()
    {
        var filament = await CreateFilamentAsync();
        var created = await CreatePrintAsync(status: PrintStatus.Printing);
        var body = Completion(filament.Id, 1_000);
        body.FilamentUsage!.Add(new CompletePrintFilamentUsageDto { FilamentId = filament.Id, AmountMg = 2_000 });

        var response = await CompleteAsync(created.Id, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Complete_UsageRowWithNoMeasurement_IsBadRequest()
    {
        var filament = await CreateFilamentAsync();
        var created = await CreatePrintAsync(status: PrintStatus.Printing);

        var response = await CompleteAsync(created.Id, new CompletePrintDto
        {
            Status = PrintStatus.Success,
            FilamentUsage = [new CompletePrintFilamentUsageDto { FilamentId = filament.Id }],
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Complete_FilamentTheCallerDoesNotOwn_IsBadRequest()
    {
        var foreign = await CreateFilamentAsync(IntegrationTestSeeder.SecondaryUserOAuthId);
        var created = await CreatePrintAsync(status: PrintStatus.Printing);

        var response = await CompleteAsync(created.Id, Completion(foreign.Id, 1_000));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Complete_MissingPrint_IsNotFound()
    {
        var response = await CompleteAsync(long.MaxValue, new CompletePrintDto { Status = PrintStatus.Success });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Complete_AnotherUsersPrint_IsForbidden()
    {
        var created = await CreatePrintAsync(status: PrintStatus.Printing, viewStatus: PrintViewStatus.Public);

        var response = await CompleteAsync(created.Id, new CompletePrintDto { Status = PrintStatus.Success },
            IntegrationTestSeeder.SecondaryUserOAuthId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(PrintStatus.Printing, (await GetDetailAsync(created.Id)).Status);
    }

    #endregion

    private static CompletePrintDto Completion(Guid filamentId, int amountMg) => new()
    {
        Status = PrintStatus.Success,
        FilamentUsage = [new CompletePrintFilamentUsageDto { FilamentId = filamentId, AmountMg = amountMg }],
    };

    private static PrintFilamentSummaryDto EstimateRow(Guid filamentId, int estimatedMg) => new()
    {
        Id = Guid.NewGuid(),
        Filament = new FilamentSummaryDto { Id = filamentId },
        EstimatedSource = PrintFilament.SourceMeasurement.Weight,
        EstimatedAmountMg = estimatedMg,
    };

    private async Task<PrintDetailDTO> CreatePrintAsync(
        string? notes = null,
        PrintStatus status = PrintStatus.Success,
        PrintViewStatus viewStatus = PrintViewStatus.Private,
        int? printTimeInSeconds = null,
        DateTimeOffset? startDate = null,
        ICollection<PrintFilamentSummaryDto>? usage = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(new AddPrintDTO
        {
            Title = $"patch-complete {Guid.NewGuid():N}",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = status,
            ViewStatus = viewStatus,
            Notes = notes,
            PrintTimeInSeconds = printTimeInSeconds,
            StartDate = startDate,
            FilamentUsage = usage ?? [],
        });
        return await ReadAsync(await _httpClient.SendAsync(request, TestContext.Current.CancellationToken));
    }

    private async Task<HttpResponseMessage> PatchAsync(long id, object body, string userOAuthId = IntegrationTestSeeder.TestUserOAuthId)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/Prints/{id}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, userOAuthId);
        request.Content = JsonContent.Create(body);
        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> CompleteAsync(long id, CompletePrintDto body, string userOAuthId = IntegrationTestSeeder.TestUserOAuthId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/Prints/{id}/complete");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, userOAuthId);
        request.Content = JsonContent.Create(body);
        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<PrintDetailDTO> ReadAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        return (await response.Content.ReadFromJsonAsync<PrintDetailDTO>(TestContext.Current.CancellationToken))!;
    }

    private async Task<PrintDetailDTO> GetDetailAsync(long id)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Prints/{id}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        return await ReadAsync(await _httpClient.SendAsync(request, TestContext.Current.CancellationToken));
    }

    /// <summary>A fresh 1 kg spool per test, so remaining-weight assertions see only that test's prints.</summary>
    private async Task<FilamentDetailDto> CreateFilamentAsync(string userOAuthId = IntegrationTestSeeder.TestUserOAuthId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, userOAuthId);
        request.Content = JsonContent.Create(new AddFilamentDto
        {
            DisplayName = $"Complete test {Guid.NewGuid():N}",
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

    private async Task<long> CreatePrinterAsync(string userOAuthId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Printers");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, userOAuthId);
        request.Content = JsonContent.Create(new AddPrinterDTO { Name = "Patch test printer", Make = "Make", Model = "Model", IsActive = true });
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<PrinterDetailDto>(TestContext.Current.CancellationToken))!.Id!.Value;
    }
}
