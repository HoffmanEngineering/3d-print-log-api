using System.Net;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Services;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// A usage row on another user's spool: no write keeps a measurement on it, and it counts toward
/// neither the spool's remaining figure nor its in-use check.
/// </summary>
public class ForeignSpoolUsageTests(CustomWebApplicationFactory factory)
    : ForeignSpoolTestBase(factory), IClassFixture<CustomWebApplicationFactory>
{
    [Fact]
    public async Task ExistingForeignUsageRow_NotifierFinish_DetachesIt()
    {
        var (victim, other) = await SeedPairAsync(otherLoadsOwnSpool: false);
        var run = $"run{Guid.NewGuid():N}";
        await NotifierAsync(other.User, "moonraker-notifier-started.json", other.Printer.Id, run);
        var started = await PrintForRunAsync(run);
        using (var scope = _factory.Services.CreateScope())
        {
            // A start recorded before the loaded rows were checked.
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var row = await db.PrintFilament.SingleAsync(r => r.PrintId == started.Id, Ct);
            row.FilamentId = victim.Filament.Id;
            await db.SaveChangesAsync(Ct);
        }

        await NotifierAsync(other.User, "moonraker-notifier-complete.json", other.Printer.Id, run, filamentUsedMm: 330_000);

        var finished = Assert.Single((await PrintForRunAsync(run)).FilamentUsage!);
        Assert.Null(finished.FilamentId);
        Assert.Equal(330, finished.LengthInM);
    }

    [Fact]
    public async Task ExistingForeignUsageRow_CompleteByRowId_DetachesIt()
    {
        var (victim, other) = await SeedPairAsync();
        var print = await SeedPrintAsync(other.User, other.Printer, victim.Filament.Id, 5_000, PrintStatus.Printing, slot: 0);
        var rowId = print.FilamentUsage!.Single().Id;

        var (status, body) = await CompleteAsync(other.User, print.Id,
            new CompletePrintFilamentUsageDto { RowId = rowId, AmountMg = 900_000, Source = PrintFilament.SourceMeasurement.Weight });

        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {body}");
        var row = Assert.Single((await PrintAsync(print.Id)).FilamentUsage!);
        Assert.Null(row.FilamentId);
        Assert.Equal(900_000, row.AmountMg);
        Assert.Equal(InitialMg - OwnPrintMg, await RemainingAsync(victim));
    }

    [Fact]
    public async Task ExistingForeignUsageRow_CompleteBySlot_DetachesIt()
    {
        var (victim, other) = await SeedPairAsync();
        var print = await SeedPrintAsync(other.User, other.Printer, victim.Filament.Id, 5_000, PrintStatus.Printing, slot: 0);

        var (status, body) = await CompleteAsync(other.User, print.Id,
            new CompletePrintFilamentUsageDto { Slot = 0, AmountMg = 700_000, Source = PrintFilament.SourceMeasurement.Weight });

        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {body}");
        var rows = (await PrintAsync(print.Id)).FilamentUsage!;
        Assert.DoesNotContain(rows, r => r.FilamentId == victim.Filament.Id);
        Assert.Contains(rows, r => r.AmountMg == 700_000);
    }

    [Fact]
    public async Task Complete_NamingAnotherUsersSpool_IsStill400()
    {
        var (victim, other) = await SeedPairAsync();
        var print = await SeedPrintAsync(other.User, other.Printer, null, 5_000, PrintStatus.Printing, slot: 0);

        var (status, _) = await CompleteAsync(other.User, print.Id,
            new CompletePrintFilamentUsageDto { FilamentId = victim.Filament.Id, AmountMg = 900_000, Source = PrintFilament.SourceMeasurement.Weight });

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Complete_OwnRowsByRowIdAndBySlot_KeepTheirSpool()
    {
        var owner = await SeedOwnerAsync();
        var byRow = await SeedPrintAsync(owner.User, owner.Printer, owner.Filament.Id, 5_000, PrintStatus.Printing, slot: 0);
        var bySlot = await SeedPrintAsync(owner.User, owner.Printer, owner.Filament.Id, 5_000, PrintStatus.Printing, slot: 0);

        var first = await CompleteAsync(owner.User, byRow.Id,
            new CompletePrintFilamentUsageDto { RowId = byRow.FilamentUsage!.Single().Id, AmountMg = 6_000, Source = PrintFilament.SourceMeasurement.Weight });
        var second = await CompleteAsync(owner.User, bySlot.Id,
            new CompletePrintFilamentUsageDto { Slot = 0, AmountMg = 7_000, Source = PrintFilament.SourceMeasurement.Weight });

        Assert.True(first.Status == HttpStatusCode.OK, first.Body);
        Assert.True(second.Status == HttpStatusCode.OK, second.Body);
        var a = Assert.Single((await PrintAsync(byRow.Id)).FilamentUsage!);
        var b = Assert.Single((await PrintAsync(bySlot.Id)).FilamentUsage!);
        Assert.Equal((owner.Filament.Id, 6_000), (a.FilamentId!.Value, a.AmountMg!.Value));
        Assert.Equal((owner.Filament.Id, 7_000), (b.FilamentId!.Value, b.AmountMg!.Value));
        Assert.Equal(InitialMg - 13_000, await RemainingAsync(owner));
    }

    [Fact]
    public async Task ExistingForeignUsageRow_DoesNotCountTowardTheOwnersRemaining()
    {
        var (victim, other) = await SeedPairAsync();
        await SeedPrintAsync(other.User, other.Printer, victim.Filament.Id, 500_000);
        var expected = InitialMg - OwnPrintMg;

        var detail = await GetJsonAsync($"/api/Filaments/{victim.Filament.Id}", victim.User);
        Assert.Equal(expected, detail.GetProperty("filamentRemaining").GetInt64());
        Assert.Equal(OwnPrintMg, detail.GetProperty("totalUsedMg").GetInt64());
        Assert.Equal(1, detail.GetProperty("printCount").GetInt32());

        var list = await GetJsonAsync("/api/Filaments?pageSize=100", victim.User);
        var item = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == victim.Filament.Id);
        Assert.Equal(expected, item.GetProperty("filamentRemaining").GetInt64());

        using var scope = _factory.Services.CreateScope();
        var filaments = scope.ServiceProvider.GetRequiredService<IFilamentService>();
        Assert.Equal(expected / 1000.0, await filaments.GetRemainingGramsForMcp(victim.User.Id, victim.Filament.Id, Ct));
    }

    [Fact]
    public async Task ExistingForeignUsageRow_DoesNotStopTheOwnerDeletingTheSpool()
    {
        var victim = await SeedOwnerAsync();
        var other = await SeedOwnerAsync();
        var foreignPrint = await SeedPrintAsync(other.User, other.Printer, victim.Filament.Id, 500_000);

        var (status, body) = await SendAsync(As(new HttpRequestMessage(HttpMethod.Delete, $"/api/Filaments/{victim.Filament.Id}"), victim.User));

        Assert.True(status == HttpStatusCode.NoContent, $"{(int)status} {body}");
        var row = Assert.Single((await PrintAsync(foreignPrint.Id)).FilamentUsage!);
        Assert.Null(row.FilamentId);
        Assert.Equal(500_000, row.AmountMg);
    }

    [Fact]
    public async Task OwnUsage_StillStopsTheOwnerDeletingTheSpool()
    {
        var (victim, _) = await SeedPairAsync();

        var (status, _) = await SendAsync(As(new HttpRequestMessage(HttpMethod.Delete, $"/api/Filaments/{victim.Filament.Id}"), victim.User));

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task PrintBySomeoneElseOnTheOwnersPrinter_StillCountsTowardTheOwnersRemaining()
    {
        // Older prints can belong to one user and sit on another's printer; the printer's owner may
        // edit them and link their own spools (PrintsController.PutPrint), so that usage is theirs.
        var (owner, creator) = await SeedPairAsync();
        await SeedPrintAsync(creator.User, owner.Printer, owner.Filament.Id, 50_000);

        Assert.Equal(InitialMg - OwnPrintMg - 50_000, await RemainingAsync(owner));
        var (status, _) = await SendAsync(As(new HttpRequestMessage(HttpMethod.Delete, $"/api/Filaments/{owner.Filament.Id}"), owner.User));
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }
}
