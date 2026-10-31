using System.Net;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.Print;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// "Other" rows (printlog-bridge#128): a usage row with no spool is filled when its spool is
/// loaded in the printer, picking its spool on the print loads it there, and a completion can
/// name the row it fills.
/// </summary>
public partial class PrinterSlotsTests
{
    #region Completing a named row (rowId)

    [Fact]
    public async Task Complete_ARowId_FillsThatRow()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var placeholder = WeightRow(null, slot: null, estimateMg: 5000, notes: "Sliced filament 1");
        var print = await CreatePrintAsync(printerId, [placeholder]);
        var rowId = Assert.Single(print.FilamentUsage!).Id;

        var completed = await CompleteAsync(print.Id, new CompletePrintDto
        {
            Status = PrintStatus.Success,
            FilamentUsage = [new CompletePrintFilamentUsageDto { RowId = rowId, Slot = 3, AmountMg = 4000 }],
        });

        var row = Assert.Single(completed.FilamentUsage!);
        Assert.Equal(3, row.Slot);
        Assert.Equal(4000, row.AmountMg);
        Assert.Equal(5000, row.EstimatedAmountMg);
        Assert.Equal("Sliced filament 1", row.Notes);
    }

    [Fact]
    public async Task Complete_ARowIdOnALinkedRow_KeepsItsSpool()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var spool = await CreateFilamentAsync();
        var print = await CreatePrintAsync(printerId, [WeightRow(spool, slot: null, estimateMg: 5000)]);
        var rowId = Assert.Single(print.FilamentUsage!).Id;

        var completed = await CompleteAsync(print.Id, new CompletePrintDto
        {
            Status = PrintStatus.Success,
            FilamentUsage = [new CompletePrintFilamentUsageDto { RowId = rowId, Slot = 3, AmountMg = 4000 }],
        });

        var row = Assert.Single(completed.FilamentUsage!);
        Assert.Equal(spool, row.Filament!.Id);
        Assert.Equal(4000, row.AmountMg);
    }

    [Fact]
    public async Task Complete_AnUnknownRowId_FallsBackToTheUsualMatch()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var print = await CreatePrintAsync(printerId, [WeightRow(null, slot: null, estimateMg: 5000)]);

        var completed = await CompleteAsync(print.Id, new CompletePrintDto
        {
            Status = PrintStatus.Success,
            FilamentUsage = [new CompletePrintFilamentUsageDto { RowId = Guid.NewGuid(), Slot = 3, AmountMg = 4000 }],
        });

        Assert.Equal(2, completed.FilamentUsage!.Count);
        Assert.Equal(4000, completed.FilamentUsage.Single(u => u.Slot == 3).AmountMg);
        Assert.Null(completed.FilamentUsage.Single(u => u.Slot is null).AmountMg);
    }

    [Fact]
    public async Task Complete_TheSameRowIdTwice_IsBadRequest()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var print = await CreatePrintAsync(printerId, [WeightRow(null, slot: null, estimateMg: 5000)]);
        var rowId = Assert.Single(print.FilamentUsage!).Id;

        var response = await SendAsync(HttpMethod.Post, $"/api/Prints/{print.Id}/complete", new CompletePrintDto
        {
            Status = PrintStatus.Success,
            FilamentUsage =
            [
                new CompletePrintFilamentUsageDto { RowId = rowId, Slot = 2, AmountMg = 1000 },
                new CompletePrintFilamentUsageDto { RowId = rowId, Slot = 3, AmountMg = 1000 },
            ],
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    #endregion

    #region Loading a spool fills an in-progress print's Other row

    [Fact]
    public async Task LoadSlot_FillsAPrintingPrintsOtherRowOnThatSlot()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var spool = await CreateFilamentAsync();
        var print = await CreatePrintAsync(printerId, [UsageRow(null, slot: 1), UsageRow(null, slot: 2)]);

        Assert.Equal(HttpStatusCode.OK, (await LoadSlotAsync(printerId, 2, spool)).StatusCode);

        var rows = (await GetPrintAsync(print.Id)).FilamentUsage!;
        Assert.Equal(spool, rows.Single(u => u.Slot == 2).Filament?.Id);
        Assert.Null(rows.Single(u => u.Slot == 1).Filament);
    }

    [Fact]
    public async Task LoadSlot_LeavesAFinishedPrintAlone()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var spool = await CreateFilamentAsync();
        var print = await CreatePrintAsync(printerId, [UsageRow(null, slot: 2)]);
        await CompleteAsync(print.Id, new CompletePrintDto { Status = PrintStatus.Success });

        await LoadSlotAsync(printerId, 2, spool);

        Assert.Null(Assert.Single((await GetPrintAsync(print.Id)).FilamentUsage!).Filament);
    }

    [Fact]
    public async Task LoadSlot_LeavesALinkedRowAlone()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var linked = await CreateFilamentAsync();
        var other = await CreateFilamentAsync();
        var print = await CreatePrintAsync(printerId, [UsageRow(linked, slot: 2)]);

        await LoadSlotAsync(printerId, 2, other);

        Assert.Equal(linked, Assert.Single((await GetPrintAsync(print.Id)).FilamentUsage!).Filament?.Id);
    }

    [Fact]
    public async Task PutPrinter_SingleSlot_FillsThePrintingPrintsOtherRow()
    {
        var printerId = await CreatePrinterAsync(slotCount: 1);
        var spool = await CreateFilamentAsync();
        var print = await CreatePrintAsync(printerId, [UsageRow(null, slot: 0)]);

        Assert.True((await PutPrinterAsync(printerId, loaded: [spool])).IsSuccessStatusCode);

        Assert.Equal(spool, Assert.Single((await GetPrintAsync(print.Id)).FilamentUsage!).Filament?.Id);
    }

    #endregion

    #region Picking a spool on the print loads it

    [Fact]
    public async Task PatchPrint_PickingASpoolForAnOtherRow_LoadsIt()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var spool = await CreateFilamentAsync();
        var print = await CreatePrintAsync(printerId, [UsageRow(null, slot: 2)]);

        await PatchRowsAsync(print.Id, (spool, 2));

        var loaded = Assert.Single(await GetLoadedAsync(printerId));
        Assert.Equal(spool, loaded.Filament!.Id);
        Assert.Equal(2, loaded.Slot);
    }

    [Fact]
    public async Task PatchPrint_OnThePrintersLatestFinishedPrint_LoadsIt()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var spool = await CreateFilamentAsync();
        var print = await CreatePrintAsync(printerId, [UsageRow(null, slot: 2)]);
        await CompleteAsync(print.Id, new CompletePrintDto { Status = PrintStatus.Success });

        await PatchRowsAsync(print.Id, (spool, 2));

        Assert.Equal(spool, Assert.Single(await GetLoadedAsync(printerId)).Filament!.Id);
    }

    [Fact]
    public async Task PatchPrint_OnAnOlderFinishedPrint_DoesNotLoad()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var spool = await CreateFilamentAsync();
        var older = await CreatePrintAsync(printerId, [UsageRow(null, slot: 2)], DateTimeOffset.UtcNow.AddDays(-2));
        await CompleteAsync(older.Id, new CompletePrintDto { Status = PrintStatus.Success });
        await CreatePrintAsync(printerId, [UsageRow(null, slot: 0)], DateTimeOffset.UtcNow.AddDays(-1));

        await PatchRowsAsync(older.Id, (spool, 2));

        Assert.Empty(await GetLoadedAsync(printerId));
    }

    [Fact]
    public async Task PatchPrint_ARowThatAlreadyHadASpool_DoesNotLoad()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var first = await CreateFilamentAsync();
        var second = await CreateFilamentAsync();
        var print = await CreatePrintAsync(printerId, [UsageRow(first, slot: 2)]);

        await PatchRowsAsync(print.Id, (second, 2));

        Assert.Empty(await GetLoadedAsync(printerId));
    }

    [Fact]
    public async Task PatchPrint_OnASingleSlotPrinter_DoesNotLoad()
    {
        var printerId = await CreatePrinterAsync(slotCount: 1);
        var spool = await CreateFilamentAsync();
        var print = await CreatePrintAsync(printerId, [UsageRow(null, slot: 0)]);

        await PatchRowsAsync(print.Id, (spool, 0));

        Assert.Empty(await GetLoadedAsync(printerId));
    }

    #endregion

    #region Fixes

    [Fact]
    public async Task CreatePrint_SingleSlot_WithNoSpoolLinked_KeepsTheLoadedSpool()
    {
        var printerId = await CreatePrinterAsync(slotCount: 1);
        var spool = await CreateFilamentAsync();
        await PutPrinterAsync(printerId, loaded: [spool]);

        await CreatePrintAsync(printerId, [UsageRow(null, slot: 0)]);

        Assert.Equal(spool, Assert.Single(await GetLoadedAsync(printerId)).Filament!.Id);
    }

    [Fact]
    public async Task ReplayedCreate_WhilePrinting_KeepsASpoolTheUserLinked()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var spool = await CreateFilamentAsync();
        var externalId = Guid.NewGuid().ToString("N");
        var print = await CreateExternalPrintAsync(printerId, externalId, [UsageRow(null, slot: 1)]);
        await PatchRowsAsync(print.Id, (spool, 1));

        await CreateExternalPrintAsync(printerId, externalId, [UsageRow(null, slot: 1)]);

        Assert.Equal(spool, Assert.Single((await GetPrintAsync(print.Id)).FilamentUsage!).Filament?.Id);
    }

    #endregion

    private static PrintFilamentSummaryDto WeightRow(Guid? filamentId, int? slot, int estimateMg, string? notes = null) => new()
    {
        Id = Guid.NewGuid(),
        Filament = filamentId is { } id ? new FilamentSummaryDto { Id = id } : null,
        Slot = slot,
        EstimatedSource = PrintFilament.SourceMeasurement.Weight,
        EstimatedAmountMg = estimateMg,
        Notes = notes,
    };

    private async Task<PrintDetailDTO> GetPrintAsync(long printId) =>
        await ReadAsync<PrintDetailDTO>(await SendAsync(HttpMethod.Get, $"/api/Prints/{printId}"));

    private async Task<PrintDetailDTO> CreatePrintAsync(
        long printerId, ICollection<PrintFilamentSummaryDto> usage, DateTimeOffset startDate) =>
        await ReadAsync<PrintDetailDTO>(await SendAsync(HttpMethod.Post, "/api/Prints", new AddPrintDTO
        {
            Title = $"slot print {Guid.NewGuid():N}",
            PrinterId = printerId,
            Status = PrintStatus.Printing,
            ViewStatus = PrintViewStatus.Private,
            StartDate = startDate,
            FilamentUsage = usage,
        }));

    private async Task<PrintDetailDTO> CreateExternalPrintAsync(
        long printerId, string externalId, ICollection<PrintFilamentSummaryDto> usage) =>
        await ReadAsync<PrintDetailDTO>(await SendAsync(HttpMethod.Post, "/api/Prints", new AddPrintDTO
        {
            Title = "bridge print",
            PrinterId = printerId,
            Status = PrintStatus.Printing,
            ViewStatus = PrintViewStatus.Private,
            ExternalSource = "printlog-bridge",
            ExternalId = externalId,
            FilamentUsage = usage,
        }));

    /// <summary>A PATCH replacing the print's rows, as the edit page sends them.</summary>
    private async Task PatchRowsAsync(long printId, params (Guid? FilamentId, int Slot)[] rows)
    {
        var response = await SendAsync(HttpMethod.Patch, $"/api/Prints/{printId}", new PatchPrintDto
        {
            FilamentUsage = rows
                .Select(r => new PutPrintFilamentSummaryDto
                {
                    FilamentId = r.FilamentId,
                    Slot = r.Slot,
                    EstimatedSource = PrintFilament.SourceMeasurement.Length,
                    EstimatedLengthInM = 1,
                    Source = PrintFilament.SourceMeasurement.Length,
                })
                .ToList(),
        });
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
    }
}
