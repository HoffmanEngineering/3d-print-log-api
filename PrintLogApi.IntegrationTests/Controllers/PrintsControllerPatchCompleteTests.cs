using System.Net;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Printer;
using PrintLogApi.Services;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// PATCH and complete (#145): a connector finishing a print must not overwrite what the user
/// edited while it ran, and must not deduct inventory twice when it retries.
/// </summary>
public class PrintsControllerPatchCompleteTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _httpClient;

    public PrintsControllerPatchCompleteTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
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

    // The edit form shows a print's legacy fields (filamentType, filamentUsageMg,
    // estimatedFilamentUsageMg) as one usage row. When it sends rows back they supersede the
    // legacy fields, or the print reports both and the deleted row comes back on reopen.

    [Fact]
    public async Task Patch_FilamentUsage_OnALegacyPrint_SupersedesTheLegacyFields()
    {
        var created = await CreateLegacyPrintAsync();

        var patched = await ReadAsync(await PatchAsync(created.Id, new
        {
            filamentUsage = new[]
            {
                new
                {
                    amountMg = 16_000,
                    estimatedAmountMg = 14_000,
                    source = PrintFilament.SourceMeasurement.Weight,
                    estimatedSource = PrintFilament.SourceMeasurement.Weight,
                    notes = "PLA",
                },
            },
        }));

        var row = Assert.Single(patched.FilamentUsage!);
        Assert.Equal(16_000, row.AmountMg);
        Assert.Null(patched.FilamentType);
        Assert.Null(patched.FilamentUsageMg);
        Assert.Null(patched.EstimatedFilamentUsageMg);
    }

    [Fact]
    public async Task Patch_EmptyFilamentUsage_OnALegacyPrint_ClearsTheLegacyFields()
    {
        var created = await CreateLegacyPrintAsync();

        var patched = await ReadAsync(await PatchAsync(created.Id, new { filamentUsage = Array.Empty<object>() }));

        Assert.Empty(patched.FilamentUsage!);
        Assert.Null(patched.FilamentType);
        Assert.Null(patched.FilamentUsageMg);
        Assert.Null(patched.EstimatedFilamentUsageMg);
    }

    [Fact]
    public async Task Patch_WithoutFilamentUsage_KeepsTheLegacyFields()
    {
        var created = await CreateLegacyPrintAsync();

        var patched = await ReadAsync(await PatchAsync(created.Id, new { title = "Renamed legacy" }));

        Assert.Equal("PLA", patched.FilamentType);
        Assert.Equal(15_000, patched.FilamentUsageMg);
        Assert.Equal(14_000, patched.EstimatedFilamentUsageMg);
    }

    [Fact]
    public async Task Patch_FilamentTheCallerDoesNotOwn_OnALegacyPrint_KeepsTheLegacyFields()
    {
        var created = await CreateLegacyPrintAsync();
        var foreign = await CreateFilamentAsync(IntegrationTestSeeder.SecondaryUserOAuthId);

        var response = await PatchAsync(created.Id, new
        {
            filamentUsage = new[] { new { filamentId = foreign.Id, amountMg = 1_000 } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var detail = await GetDetailAsync(created.Id);
        Assert.Equal("PLA", detail.FilamentType);
        Assert.Equal(15_000, detail.FilamentUsageMg);
        Assert.Equal(14_000, detail.EstimatedFilamentUsageMg);
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

    [Theory]
    [InlineData("printTimeInSeconds")]
    [InlineData("estimatedPrintTimeInSeconds")]
    public async Task Patch_NegativeDuration_IsBadRequest(string field)
    {
        var created = await CreatePrintAsync();

        var response = await PatchAsync(created.Id, new Dictionary<string, object> { [field] = -1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("amountMg")]
    [InlineData("estimatedAmountMg")]
    [InlineData("lengthInM")]
    [InlineData("estimatedLengthInM")]
    [InlineData("volumeMl")]
    [InlineData("estimatedVolumeMl")]
    [InlineData("slot")]
    public async Task Patch_NegativeUsageValue_IsBadRequest(string field)
    {
        var created = await CreatePrintAsync();

        var response = await PatchAsync(created.Id, new
        {
            filamentUsage = new[] { new Dictionary<string, object> { ["amountMg"] = 1_000, [field] = -1 } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Patch_MoreUsageRowsThanTheCap_IsBadRequest()
    {
        var created = await CreatePrintAsync();

        var response = await PatchAsync(created.Id, new
        {
            filamentUsage = Enumerable.Range(0, CompletePrintDto.MaxFilamentUsageRows + 1)
                .Select(_ => new { amountMg = 1_000 })
                .ToArray(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("lengthInM")]
    [InlineData("estimatedLengthInM")]
    [InlineData("volumeMl")]
    [InlineData("estimatedVolumeMl")]
    public async Task Patch_UsageValueAboveTheCap_IsBadRequest(string field)
    {
        var created = await CreatePrintAsync();

        var response = await PatchAsync(created.Id, new
        {
            filamentUsage = new[] { new Dictionary<string, object> { ["amountMg"] = 1_000, [field] = 1e300 } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Patch_NullUsageRow_IsBadRequest()
    {
        var created = await CreatePrintAsync();

        var response = await PatchAsync(created.Id, new { filamentUsage = new object?[] { null } });

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
    public async Task Complete_SpoolOnASlotWithAnUnlinkedRow_LinksThatRow()
    {
        var filament = await CreateFilamentAsync();
        var estimate = UnlinkedEstimateRow(slot: 0, estimatedMg: 12_780);
        estimate.Notes = "loaded mid-print";
        var created = await CreatePrintAsync(status: PrintStatus.Printing, usage: [estimate]);

        var completed = await ReadAsync(await CompleteAsync(created.Id, SlotCompletion(0, filament.Id, 12_760)));

        var row = Assert.Single(completed.FilamentUsage!);
        Assert.Equal(filament.Id, row.Filament?.Id);
        Assert.Equal(0, row.Slot);
        Assert.Equal(12_780, row.EstimatedAmountMg);
        Assert.Equal(12_760, row.AmountMg);
        Assert.Equal("loaded mid-print", row.Notes);
        Assert.Equal(PrintFilament.SourceMeasurement.Weight, row.Source);
        Assert.Equal(1_000_000 - 12_760, await GetRemainingAsync(filament.Id));
    }

    [Fact]
    public async Task Complete_SpoolOnASlotWithAnUnlinkedRow_ByLength_DerivesTheWeightAndRemaining()
    {
        var filament = await CreateFilamentAsync();
        var created = await CreatePrintAsync(status: PrintStatus.Printing, usage: [UnlinkedEstimateRow(slot: 0, estimatedMg: 12_780)]);

        var completed = await ReadAsync(await CompleteAsync(created.Id, new CompletePrintDto
        {
            Status = PrintStatus.Success,
            FilamentUsage = [new CompletePrintFilamentUsageDto { Slot = 0, FilamentId = filament.Id, LengthInM = 4.2784 }],
        }));

        var row = Assert.Single(completed.FilamentUsage!);
        Assert.Equal(filament.Id, row.Filament?.Id);
        Assert.Equal(PrintFilament.SourceMeasurement.Length, row.Source);
        Assert.Equal(12_780, row.EstimatedAmountMg);
        Assert.InRange(row.AmountMg!.Value, 12_500, 13_000); // 1.75 mm PLA at 1.24 g/cm3 is ~2.98 g/m
        Assert.Equal(1_000_000 - row.AmountMg.Value, await GetRemainingAsync(filament.Id));
    }

    [Fact]
    public async Task Complete_RowAlreadyOnTheSpool_MatchesBySpoolOnAnySlot()
    {
        var filament = await CreateFilamentAsync();
        var linked = EstimateRow(filament.Id, 20_000);
        linked.Slot = 1;
        var created = await CreatePrintAsync(
            status: PrintStatus.Printing,
            usage: [UnlinkedEstimateRow(slot: 0, estimatedMg: 5_000), linked]);

        var completed = await ReadAsync(await CompleteAsync(created.Id, SlotCompletion(0, filament.Id, 21_000)));

        Assert.Equal(2, completed.FilamentUsage!.Count);
        var spoolRow = Assert.Single(completed.FilamentUsage, r => r.Filament?.Id == filament.Id);
        Assert.Equal(1, spoolRow.Slot);
        Assert.Equal(20_000, spoolRow.EstimatedAmountMg);
        Assert.Equal(21_000, spoolRow.AmountMg);
        var unlinkedRow = Assert.Single(completed.FilamentUsage, r => r.Filament == null);
        Assert.Equal(0, unlinkedRow.Slot);
        Assert.Equal(5_000, unlinkedRow.EstimatedAmountMg);
        Assert.Null(unlinkedRow.AmountMg);
    }

    [Fact]
    public async Task Complete_SpoolOnASlotTwice_IsIdempotent()
    {
        var filament = await CreateFilamentAsync();
        var created = await CreatePrintAsync(status: PrintStatus.Printing, usage: [UnlinkedEstimateRow(slot: 0, estimatedMg: 12_780)]);
        var body = SlotCompletion(0, filament.Id, 12_760);

        await ReadAsync(await CompleteAsync(created.Id, body));
        var afterFirst = await GetRemainingAsync(filament.Id);
        var completed = await ReadAsync(await CompleteAsync(created.Id, body));

        var row = Assert.Single(completed.FilamentUsage!);
        Assert.Equal(filament.Id, row.Filament?.Id);
        Assert.Equal(12_780, row.EstimatedAmountMg);
        Assert.Equal(12_760, row.AmountMg);
        Assert.Equal(1_000_000 - 12_760, afterFirst);
        Assert.Equal(afterFirst, await GetRemainingAsync(filament.Id));
    }

    [Fact]
    public async Task Complete_SpoolOnASlot_LeavesUnlinkedRowsOnOtherSlotsAlone()
    {
        var filament = await CreateFilamentAsync();
        var created = await CreatePrintAsync(
            status: PrintStatus.Printing,
            usage: [UnlinkedEstimateRow(slot: 0, estimatedMg: 5_000), UnlinkedEstimateRow(slot: 1, estimatedMg: 7_000)]);

        var completed = await ReadAsync(await CompleteAsync(created.Id, SlotCompletion(1, filament.Id, 6_500)));

        Assert.Equal(2, completed.FilamentUsage!.Count);
        var slot0 = Assert.Single(completed.FilamentUsage, r => r.Slot == 0);
        Assert.Null(slot0.Filament);
        Assert.Equal(5_000, slot0.EstimatedAmountMg);
        Assert.Null(slot0.AmountMg);
        var slot1 = Assert.Single(completed.FilamentUsage, r => r.Slot == 1);
        Assert.Equal(filament.Id, slot1.Filament?.Id);
        Assert.Equal(7_000, slot1.EstimatedAmountMg);
        Assert.Equal(6_500, slot1.AmountMg);
    }

    [Fact]
    public async Task Complete_TwoSpoolsOnOneSlot_ClaimTheUnlinkedRowOnce()
    {
        var first = await CreateFilamentAsync();
        var second = await CreateFilamentAsync();
        var created = await CreatePrintAsync(status: PrintStatus.Printing, usage: [UnlinkedEstimateRow(slot: 0, estimatedMg: 12_000)]);

        var completed = await ReadAsync(await CompleteAsync(created.Id, new CompletePrintDto
        {
            Status = PrintStatus.Success,
            FilamentUsage =
            [
                new CompletePrintFilamentUsageDto { Slot = 0, FilamentId = first.Id, AmountMg = 8_000 },
                new CompletePrintFilamentUsageDto { Slot = 0, FilamentId = second.Id, AmountMg = 4_000 },
            ],
        }));

        Assert.Equal(2, completed.FilamentUsage!.Count);
        Assert.DoesNotContain(completed.FilamentUsage, r => r.Filament == null);
        var firstRow = Assert.Single(completed.FilamentUsage, r => r.Filament?.Id == first.Id);
        Assert.Equal(12_000, firstRow.EstimatedAmountMg);
        Assert.Equal(8_000, firstRow.AmountMg);
        var secondRow = Assert.Single(completed.FilamentUsage, r => r.Filament?.Id == second.Id);
        Assert.Null(secondRow.EstimatedAmountMg);
        Assert.Equal(4_000, secondRow.AmountMg);
        Assert.Equal(1_000_000 - 8_000, await GetRemainingAsync(first.Id));
        Assert.Equal(1_000_000 - 4_000, await GetRemainingAsync(second.Id));
    }

    [Fact]
    public async Task Complete_SlotOnlyRowClaimsTheUnlinkedRow_SoASpoolOnThatSlotAddsARow()
    {
        var filament = await CreateFilamentAsync();
        var created = await CreatePrintAsync(status: PrintStatus.Printing, usage: [UnlinkedEstimateRow(slot: 0, estimatedMg: 12_000)]);

        // The spool row comes first, so only claiming slot-only rows up front keeps the two apart.
        var completed = await ReadAsync(await CompleteAsync(created.Id, new CompletePrintDto
        {
            Status = PrintStatus.Success,
            FilamentUsage =
            [
                new CompletePrintFilamentUsageDto { Slot = 0, FilamentId = filament.Id, AmountMg = 3_000 },
                new CompletePrintFilamentUsageDto { Slot = 0, AmountMg = 9_000 },
            ],
        }));

        Assert.Equal(2, completed.FilamentUsage!.Count);
        var unlinkedRow = Assert.Single(completed.FilamentUsage, r => r.Filament == null);
        Assert.Equal(12_000, unlinkedRow.EstimatedAmountMg);
        Assert.Equal(9_000, unlinkedRow.AmountMg);
        var spoolRow = Assert.Single(completed.FilamentUsage, r => r.Filament?.Id == filament.Id);
        Assert.Null(spoolRow.EstimatedAmountMg);
        Assert.Equal(3_000, spoolRow.AmountMg);
    }

    [Fact]
    public async Task Complete_InOrderRowBeforeASpoolOnASlot_DoesNotTakeThatSlotsRow()
    {
        var filament = await CreateFilamentAsync();
        var created = await CreatePrintAsync(status: PrintStatus.Printing, usage: [UnlinkedEstimateRow(slot: 0, estimatedMg: 12_000)]);

        var completed = await ReadAsync(await CompleteAsync(created.Id, new CompletePrintDto
        {
            Status = PrintStatus.Success,
            FilamentUsage =
            [
                new CompletePrintFilamentUsageDto { AmountMg = 2_000 },
                new CompletePrintFilamentUsageDto { Slot = 0, FilamentId = filament.Id, AmountMg = 11_000 },
            ],
        }));

        Assert.Equal(2, completed.FilamentUsage!.Count);
        var spoolRow = Assert.Single(completed.FilamentUsage, r => r.Filament?.Id == filament.Id);
        Assert.Equal(0, spoolRow.Slot);
        Assert.Equal(12_000, spoolRow.EstimatedAmountMg);
        Assert.Equal(11_000, spoolRow.AmountMg);
        var inOrderRow = Assert.Single(completed.FilamentUsage, r => r.Filament == null);
        Assert.Null(inOrderRow.EstimatedAmountMg);
        Assert.Equal(2_000, inOrderRow.AmountMg);
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
    public async Task Create_LengthWhoseWeightOverflowsAnInt_StoresAClampedWeight()
    {
        // POST has no length bound. 1,000 km of 1.75 mm PLA is ~2.98e9 mg, which the unchecked
        // long-to-int cast used to wrap to a negative weight (#198).
        var filament = await CreateFilamentAsync();

        var created = await CreatePrintAsync(usage:
        [
            new PrintFilamentSummaryDto
            {
                Id = Guid.NewGuid(),
                Filament = new FilamentSummaryDto { Id = filament.Id },
                Source = PrintFilament.SourceMeasurement.Length,
                LengthInM = 1_000_000,
            },
        ]);

        Assert.Equal(int.MaxValue, Assert.Single(created.FilamentUsage!).AmountMg);
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
    [InlineData(0)]
    // The UTC instant minus the duration is representable here, but the local clock time is not,
    // and AddSeconds checks both.
    [InlineData(-5)]
    public async Task Complete_EndedAtTooCloseToTheMinimumForTheDuration_IsBadRequest(int offsetHours)
    {
        // Returned 500 before #198: deriving the start date ran off the start of DateTimeOffset.
        var created = await CreatePrintAsync(status: PrintStatus.Printing, startDate: null);

        var response = await CompleteAsync(created.Id, new CompletePrintDto
        {
            Status = PrintStatus.Success,
            EndedAt = new DateTimeOffset(1, 1, 1, 0, 30, 0, TimeSpan.FromHours(offsetHours)),
            PrintTimeInSeconds = 3_600,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Complete_EndedAtTooFarAfterTheStartDate_IsBadRequest()
    {
        var start = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var created = await CreatePrintAsync(status: PrintStatus.Printing, startDate: start);

        var response = await CompleteAsync(created.Id, new CompletePrintDto
        {
            Status = PrintStatus.Success,
            EndedAt = DateTimeOffset.MaxValue,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null((await GetDetailAsync(created.Id)).PrintTimeInSeconds);
    }

    [Theory]
    [InlineData("amountMg")]
    [InlineData("lengthInM")]
    [InlineData("volumeMl")]
    [InlineData("slot")]
    public async Task Complete_NegativeUsageValue_IsBadRequest(string field)
    {
        var created = await CreatePrintAsync(status: PrintStatus.Printing);
        var row = new CompletePrintFilamentUsageDto { AmountMg = 1_000 };
        switch (field)
        {
            case "amountMg": row.AmountMg = -1; break;
            case "lengthInM": row.LengthInM = -0.5; break;
            case "volumeMl": row.VolumeMl = -0.5; break;
            case "slot": row.Slot = -1; break;
        }

        var response = await CompleteAsync(created.Id, new CompletePrintDto
        {
            Status = PrintStatus.Success,
            FilamentUsage = [row],
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("lengthInM")]
    [InlineData("volumeMl")]
    public async Task Complete_UsageValueAboveTheCap_IsBadRequest(string field)
    {
        var created = await CreatePrintAsync(status: PrintStatus.Printing);
        var row = field == "lengthInM"
            ? new CompletePrintFilamentUsageDto { LengthInM = 1e300 }
            : new CompletePrintFilamentUsageDto { VolumeMl = 1e300 };

        var response = await CompleteAsync(created.Id, new CompletePrintDto
        {
            Status = PrintStatus.Success,
            FilamentUsage = [row],
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Complete_NullUsageRow_IsBadRequest()
    {
        var created = await CreatePrintAsync(status: PrintStatus.Printing);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/Prints/{created.Id}/complete");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(new { status = PrintStatus.Success, filamentUsage = new object?[] { null } });

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Complete_MoreUsageRowsThanTheCap_IsBadRequest()
    {
        var created = await CreatePrintAsync(status: PrintStatus.Printing);

        var response = await CompleteAsync(created.Id, new CompletePrintDto
        {
            Status = PrintStatus.Success,
            FilamentUsage = Enumerable.Range(0, CompletePrintDto.MaxFilamentUsageRows + 1)
                .Select(_ => new CompletePrintFilamentUsageDto { LengthInM = 1 })
                .ToList(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await GetDetailAsync(created.Id)).FilamentUsage!);
    }

    [Fact]
    public async Task Complete_UsageRowsAtTheCap_AreAccepted()
    {
        var created = await CreatePrintAsync(status: PrintStatus.Printing);

        var completed = await ReadAsync(await CompleteAsync(created.Id, new CompletePrintDto
        {
            Status = PrintStatus.Success,
            FilamentUsage = Enumerable.Range(0, CompletePrintDto.MaxFilamentUsageRows)
                .Select(_ => new CompletePrintFilamentUsageDto { LengthInM = 1 })
                .ToList(),
        }));

        Assert.Equal(CompletePrintDto.MaxFilamentUsageRows, completed.FilamentUsage!.Count);
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

    [Fact]
    public async Task Patch_AsPrinterOwnerWhoDidNotCreateThePrint_SucceedsAndInvalidatesTheCreatorsCache()
    {
        // The secondary user's print on the test user's printer: the caller owns the printer only.
        var printId = await InsertPrintAsync(IntegrationTestSeeder.TestPrinterId, IntegrationTestSeeder.SecondaryUserId);
        var creatorVersionBefore = CacheVersionOf(IntegrationTestSeeder.SecondaryUserId);

        var response = await PatchAsync(printId, new { title = "Renamed by the printer's owner" });

        Assert.Equal("Renamed by the printer's owner", (await ReadAsync(response)).Title);
        Assert.NotEqual(creatorVersionBefore, CacheVersionOf(IntegrationTestSeeder.SecondaryUserId));
    }

    [Fact]
    public async Task Complete_AsCreatorOnAnotherUsersPrinter_SucceedsAndInvalidatesThePrinterOwnersCache()
    {
        // The test user's print on the secondary user's printer: the caller created the print only.
        var foreignPrinterId = await CreatePrinterAsync(IntegrationTestSeeder.SecondaryUserOAuthId);
        var printId = await InsertPrintAsync(foreignPrinterId, IntegrationTestSeeder.TestUserId);
        var printerOwnerVersionBefore = CacheVersionOf(IntegrationTestSeeder.SecondaryUserId);

        var response = await CompleteAsync(printId, new CompletePrintDto { Status = PrintStatus.Success });

        Assert.Equal(PrintStatus.Success, (await ReadAsync(response)).Status);
        Assert.NotEqual(printerOwnerVersionBefore, CacheVersionOf(IntegrationTestSeeder.SecondaryUserId));
    }

    #endregion

    #region complete notifications (#166)

    [Fact]
    public async Task Complete_PrintingPrintAsSuccess_SendsOneCompletedNotification()
    {
        var created = await CreatePrintAsync(status: PrintStatus.Printing);

        await ReadAsync(await CompleteAsync(created.Id, new CompletePrintDto { Status = PrintStatus.Success }));

        var notification = Assert.Single(await NotificationsForPrintAsync(created.Id));
        Assert.Equal(NotificationType.PrintCompleted, notification.Type);
        Assert.Equal(IntegrationTestSeeder.TestUserId, notification.UserId);
    }

    [Theory]
    [InlineData(PrintStatus.Failed)]
    [InlineData(PrintStatus.Cancelled)]
    [InlineData(PrintStatus.PartialSuccess)]
    public async Task Complete_PrintingPrintAsAnythingButSuccess_SendsOneFailedNotification(PrintStatus status)
    {
        // The same split the event pipeline makes: only Success is "completed".
        var created = await CreatePrintAsync(status: PrintStatus.Printing);

        await ReadAsync(await CompleteAsync(created.Id, new CompletePrintDto { Status = status }));

        var notification = Assert.Single(await NotificationsForPrintAsync(created.Id));
        Assert.Equal(NotificationType.PrintFailed, notification.Type);
        Assert.Equal(IntegrationTestSeeder.TestUserId, notification.UserId);
    }

    [Fact]
    public async Task Complete_Retried_DoesNotNotifyAgain()
    {
        var created = await CreatePrintAsync(status: PrintStatus.Printing);
        var body = new CompletePrintDto { Status = PrintStatus.Success, PrintTimeInSeconds = 600 };

        await ReadAsync(await CompleteAsync(created.Id, body));
        await ReadAsync(await CompleteAsync(created.Id, body));

        Assert.Single(await NotificationsForPrintAsync(created.Id));
    }

    [Theory]
    [InlineData(PrintStatus.Success, PrintStatus.Failed)]
    [InlineData(PrintStatus.Failed, PrintStatus.Success)]
    [InlineData(PrintStatus.Cancelled, PrintStatus.Cancelled)]
    public async Task Complete_AlreadyFinishedPrint_DoesNotNotify(PrintStatus existing, PrintStatus completion)
    {
        var created = await CreatePrintAsync(status: existing);

        var completed = await ReadAsync(await CompleteAsync(created.Id, new CompletePrintDto { Status = completion }));

        Assert.Equal(completion, completed.Status);
        Assert.Empty(await NotificationsForPrintAsync(created.Id));
    }

    [Fact]
    public async Task Complete_PendingPrint_Notifies()
    {
        // Pending is not finished either, so finishing it is still the transition.
        var created = await CreatePrintAsync(status: PrintStatus.Pending);

        await ReadAsync(await CompleteAsync(created.Id, new CompletePrintDto { Status = PrintStatus.Success }));

        Assert.Equal(NotificationType.PrintCompleted, Assert.Single(await NotificationsForPrintAsync(created.Id)).Type);
    }

    [Fact]
    public async Task Complete_NotificationFails_StillCompletesThePrint()
    {
        using var failing = _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddTransient<INotificationService>(sp =>
                FailingPrintNotifications.Wrap(ActivatorUtilities.CreateInstance<NotificationService>(sp)))));
        var created = await CreatePrintAsync(status: PrintStatus.Printing);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/Prints/{created.Id}/complete");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(new CompletePrintDto { Status = PrintStatus.Success });
        var response = await failing.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(PrintStatus.Success, (await ReadAsync(response)).Status);
        Assert.Equal(PrintStatus.Success, (await GetDetailAsync(created.Id)).Status);
        Assert.Empty(await NotificationsForPrintAsync(created.Id));
    }

    [Fact]
    public async Task Complete_LosesTheFinishRaceTwice_Returns409()
    {
        // Losing twice needs the print re-opened and re-finished between the retry's read and its
        // save, which no deterministic interleaving reaches cheaply. PrintFinishRaceTests covers
        // the service's single loss; this pins only how the controller maps the second one.
        using var racing = _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddTransient<IPrintService>(sp =>
                AlwaysLosingCompletion.Wrap(ActivatorUtilities.CreateInstance<PrintService>(sp)))));
        var created = await CreatePrintAsync(status: PrintStatus.Printing);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/Prints/{created.Id}/complete");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(new CompletePrintDto { Status = PrintStatus.Success });
        var response = await racing.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(PrintStatus.Printing, (await GetDetailAsync(created.Id)).Status);
    }

    #endregion

    /// <summary>
    /// A Printing print inserted directly, so its creator and its printer's owner can differ,
    /// which the API never creates.
    /// </summary>
    private async Task<long> InsertPrintAsync(long printerId, long creatorId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var now = DateTime.UtcNow;
        var print = new Print
        {
            Title = $"cross-owner {Guid.NewGuid():N}",
            PrinterId = printerId,
            Status = PrintStatus.Printing,
            ViewStatus = PrintViewStatus.Private,
            CreatedById = creatorId,
            CreatedDate = now,
            UpdatedById = creatorId,
            UpdatedDate = now,
        };
        db.Prints.Add(print);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return print.Id;
    }

    private string CacheVersionOf(long userId)
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ICacheVersionService>().GetUserCacheVersion(userId);
    }

    private async Task<List<Notification>> NotificationsForPrintAsync(long printId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.Notifications
            .Where(n => n.PrintId == printId)
            .AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A notification service whose print-finish notifications throw; every other call passes through.</summary>
    public class FailingPrintNotifications : DispatchProxy
    {
        private INotificationService _inner = null!;

        public static INotificationService Wrap(INotificationService inner)
        {
            var proxy = Create<INotificationService, FailingPrintNotifications>();
            ((FailingPrintNotifications)(object)proxy)._inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name is nameof(INotificationService.CreatePrintCompletedNotification)
                or nameof(INotificationService.CreatePrintFailedNotification))
            {
                throw new InvalidOperationException("Notification store unavailable.");
            }
            return targetMethod.Invoke(_inner, args);
        }
    }

    /// <summary>A print service whose completions fault as a twice-lost finish race (#199); every other call passes through.</summary>
    public class AlwaysLosingCompletion : DispatchProxy
    {
        private IPrintService _inner = null!;

        public static IPrintService Wrap(IPrintService inner)
        {
            var proxy = Create<IPrintService, AlwaysLosingCompletion>();
            ((AlwaysLosingCompletion)(object)proxy)._inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name is nameof(IPrintService.CompletePrint))
            {
                return Task.FromException<Print>(
                    new DbUpdateConcurrencyException("The print changed while it was being completed."));
            }
            return targetMethod.Invoke(_inner, args);
        }
    }

    private static CompletePrintDto Completion(Guid filamentId, int amountMg) => new()
    {
        Status = PrintStatus.Success,
        FilamentUsage = [new CompletePrintFilamentUsageDto { FilamentId = filamentId, AmountMg = amountMg }],
    };

    private static CompletePrintDto SlotCompletion(int slot, Guid filamentId, int amountMg) => new()
    {
        Status = PrintStatus.Success,
        FilamentUsage = [new CompletePrintFilamentUsageDto { Slot = slot, FilamentId = filamentId, AmountMg = amountMg }],
    };

    /// <summary>An estimate on a slot with no spool linked, as a print started before a spool was loaded has.</summary>
    private static PrintFilamentSummaryDto UnlinkedEstimateRow(int slot, int estimatedMg) => new()
    {
        Id = Guid.NewGuid(),
        Slot = slot,
        EstimatedSource = PrintFilament.SourceMeasurement.Weight,
        EstimatedAmountMg = estimatedMg,
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

    /// <summary>A print recorded before usage rows existed: only the legacy scalar fields.</summary>
    private async Task<PrintDetailDTO> CreateLegacyPrintAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(new AddPrintDTO
        {
            Title = $"patch-legacy {Guid.NewGuid():N}",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Success,
            ViewStatus = PrintViewStatus.Private,
            FilamentType = "PLA",
            FilamentUsageMg = 15_000,
            EstimatedFilamentUsageMg = 14_000,
            FilamentUsage = [],
        });
        var created = await ReadAsync(await _httpClient.SendAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal("PLA", created.FilamentType);
        Assert.Equal(15_000, created.FilamentUsageMg);
        Assert.Empty(created.FilamentUsage!);
        return created;
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
