using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Printer;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// A full PUT carries the parent's child rows with their ids. A row id the parent does not own
/// (another user's row, read off a public print, or one this parent no longer has) must become a
/// new row of the caller's own: never an UPDATE that moves someone else's row onto the caller's
/// parent, and never a 500. The parent's own rows keep their ids.
/// </summary>
public class ChildRowOwnershipTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Owner(User User, Printer Printer, Filament Filament);

    // ---- Prints: PUT /api/Prints/{id}, FilamentUsage ----

    [Fact]
    public async Task PutPrint_WithAnotherUsersUsageRowId_AddsOwnRowAndLeavesVictimRow()
    {
        var victim = await SeedUserAsync();
        var attacker = await SeedUserAsync();
        var victimPrint = await SeedPrintAsync(victim, PrintViewStatus.Public, 50_000);
        var attackerPrint = await SeedPrintAsync(attacker, PrintViewStatus.Private, 1_000);
        var victimRowId = victimPrint.FilamentUsage!.Single().Id;
        var remainingBefore = await RemainingAsync(victim);

        var put = await PutPrintAsync(attacker, attackerPrint,
            [new PutPrintFilamentSummaryDto { Id = victimRowId, FilamentId = null, AmountMg = 1, Notes = "taken" }]);

        Assert.True(put.IsSuccessStatusCode, $"PUT returned {(int)put.StatusCode}");
        await AssertVictimRowUntouchedAsync(victimRowId, victimPrint.Id, victim.Filament.Id, 50_000);
        Assert.Equal(remainingBefore, await RemainingAsync(victim));

        var attackerRows = await UsageRowsAsync(attackerPrint.Id);
        var row = Assert.Single(attackerRows);
        Assert.NotEqual(victimRowId, row.Id);
        Assert.Equal(1, row.AmountMg);
        Assert.Equal("taken", row.Notes);
    }

    [Fact]
    public async Task PutPrint_WithAnotherUsersUsageRowIdBesideOwnRow_KeepsOwnRowIdAndAddsNewRow()
    {
        var victim = await SeedUserAsync();
        var attacker = await SeedUserAsync();
        var victimPrint = await SeedPrintAsync(victim, PrintViewStatus.Unlisted, 30_000);
        var attackerPrint = await SeedPrintAsync(attacker, PrintViewStatus.Private, 1_000);
        var victimRowId = victimPrint.FilamentUsage!.Single().Id;
        var attackerRowId = attackerPrint.FilamentUsage!.Single().Id;

        var put = await PutPrintAsync(attacker, attackerPrint,
        [
            new PutPrintFilamentSummaryDto { Id = attackerRowId, FilamentId = attacker.Filament.Id, AmountMg = 1_000 },
            new PutPrintFilamentSummaryDto { Id = victimRowId, FilamentId = attacker.Filament.Id, AmountMg = 2 },
        ]);

        Assert.True(put.IsSuccessStatusCode, $"PUT returned {(int)put.StatusCode}");
        await AssertVictimRowUntouchedAsync(victimRowId, victimPrint.Id, victim.Filament.Id, 30_000);

        var attackerRows = await UsageRowsAsync(attackerPrint.Id);
        Assert.Equal(2, attackerRows.Count);
        Assert.Contains(attackerRows, r => r.Id == attackerRowId && r.AmountMg == 1_000);
        Assert.Contains(attackerRows, r => r.Id != attackerRowId && r.Id != victimRowId && r.AmountMg == 2);
    }

    [Fact]
    public async Task PutPrint_WithUnknownUsageRowId_AddsRowInsteadOfFailing()
    {
        // A stale id: the edit form was open while a connector's PATCH replaced the rows.
        var owner = await SeedUserAsync();
        var print = await SeedPrintAsync(owner, PrintViewStatus.Private, 1_000);
        var staleId = Guid.NewGuid();

        var put = await PutPrintAsync(owner, print,
            [new PutPrintFilamentSummaryDto { Id = staleId, FilamentId = owner.Filament.Id, AmountMg = 5 }]);

        Assert.True(put.IsSuccessStatusCode, $"PUT returned {(int)put.StatusCode}");
        var row = Assert.Single(await UsageRowsAsync(print.Id));
        Assert.Equal(5, row.AmountMg);
        Assert.Equal(owner.Filament.Id, row.FilamentId);
    }

    [Fact]
    public async Task PutPrint_EditingOwnRows_KeepsIdsAddsAndRemovesRows()
    {
        var owner = await SeedUserAsync();
        var print = await SeedPrintAsync(owner, PrintViewStatus.Private, 1_000, extraRowMg: 2_000);
        var kept = print.FilamentUsage!.Single(r => r.AmountMg == 1_000).Id;
        var dropped = print.FilamentUsage!.Single(r => r.AmountMg == 2_000).Id;

        // What the edit form sends: its own row ids, and EMPTY_GUID for a row it added.
        var put = await PutPrintAsync(owner, print,
        [
            new PutPrintFilamentSummaryDto { Id = kept, FilamentId = owner.Filament.Id, AmountMg = 1_500 },
            new PutPrintFilamentSummaryDto { Id = Guid.Empty, FilamentId = null, AmountMg = 300, Notes = "new" },
        ]);

        Assert.True(put.IsSuccessStatusCode, $"PUT returned {(int)put.StatusCode}");
        var rows = await UsageRowsAsync(print.Id);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Id == kept && r.AmountMg == 1_500);
        Assert.Contains(rows, r => r.Id != kept && r.Id != dropped && r.AmountMg == 300 && r.Notes == "new");
        Assert.DoesNotContain(rows, r => r.Id == dropped);
    }

    [Fact]
    public async Task PutPrint_WithOwnUsageRowIdTwice_KeepsOneAndAddsTheOther()
    {
        var owner = await SeedUserAsync();
        var print = await SeedPrintAsync(owner, PrintViewStatus.Private, 1_000);
        var rowId = print.FilamentUsage!.Single().Id;

        var put = await PutPrintAsync(owner, print,
        [
            new PutPrintFilamentSummaryDto { Id = rowId, FilamentId = owner.Filament.Id, AmountMg = 1_000 },
            new PutPrintFilamentSummaryDto { Id = rowId, FilamentId = owner.Filament.Id, AmountMg = 400 },
        ]);

        Assert.True(put.IsSuccessStatusCode, $"PUT returned {(int)put.StatusCode}");
        var rows = await UsageRowsAsync(print.Id);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Id == rowId && r.AmountMg == 1_000);
        Assert.Contains(rows, r => r.Id != rowId && r.AmountMg == 400);
    }

    // ---- Filaments: PUT /api/Filaments/{id}, FilamentAdjustments ----

    [Fact]
    public async Task PutFilament_WithAnotherUsersAdjustmentId_AddsOwnAdjustmentAndLeavesVictims()
    {
        var victim = await SeedUserAsync();
        var attacker = await SeedUserAsync();
        var victimAdjustmentId = await SeedAdjustmentAsync(victim, -70_000, "victim note");
        var remainingBefore = await RemainingAsync(victim);

        var put = await PutFilamentAsync(attacker,
            [Adjustment(victimAdjustmentId, attacker.Filament.Id, 5_000, "taken")]);

        Assert.True(put.IsSuccessStatusCode, $"PUT returned {(int)put.StatusCode}: {await put.Content.ReadAsStringAsync(Ct)}");
        var victimRow = await AdjustmentAsync(victimAdjustmentId);
        Assert.NotNull(victimRow);
        Assert.Equal(victim.Filament.Id, victimRow!.FilamentId);
        Assert.Equal(-70_000, victimRow.AmountMg);
        Assert.Equal("victim note", victimRow.Notes);
        Assert.Equal(remainingBefore, await RemainingAsync(victim));

        var attackerRow = Assert.Single(await AdjustmentsAsync(attacker.Filament.Id));
        Assert.NotEqual(victimAdjustmentId, attackerRow.Id);
        Assert.Equal(5_000, attackerRow.AmountMg);
    }

    [Fact]
    public async Task PutFilament_WithUnknownAdjustmentId_AddsAdjustmentInsteadOfFailing()
    {
        var owner = await SeedUserAsync();

        var put = await PutFilamentAsync(owner, [Adjustment(Guid.NewGuid(), owner.Filament.Id, -3_000, null)]);

        Assert.True(put.IsSuccessStatusCode, $"PUT returned {(int)put.StatusCode}: {await put.Content.ReadAsStringAsync(Ct)}");
        var row = Assert.Single(await AdjustmentsAsync(owner.Filament.Id));
        Assert.Equal(-3_000, row.AmountMg);
    }

    [Fact]
    public async Task PutFilament_EditingOwnAdjustments_KeepsIdsAddsAndRemoves()
    {
        var owner = await SeedUserAsync();
        var kept = await SeedAdjustmentAsync(owner, -10_000, "kept");
        var dropped = await SeedAdjustmentAsync(owner, -20_000, "dropped");

        var put = await PutFilamentAsync(owner,
        [
            Adjustment(kept, owner.Filament.Id, -15_000, "kept"),
            Adjustment(Guid.Empty, owner.Filament.Id, 4_000, "new"),
        ]);

        Assert.True(put.IsSuccessStatusCode, $"PUT returned {(int)put.StatusCode}: {await put.Content.ReadAsStringAsync(Ct)}");
        var rows = await AdjustmentsAsync(owner.Filament.Id);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Id == kept && r.AmountMg == -15_000);
        Assert.Contains(rows, r => r.Id != kept && r.Id != dropped && r.AmountMg == 4_000 && r.Notes == "new");
    }

    [Fact]
    public async Task PutFilament_WithAdjustmentNamingAnotherSpool_KeepsItOnTheEditedSpool()
    {
        // The row's filamentId is in the request too; the spool being edited owns its rows.
        var victim = await SeedUserAsync();
        var attacker = await SeedUserAsync();
        var own = await SeedAdjustmentAsync(attacker, -1_000, "own");
        var victimRemainingBefore = await RemainingAsync(victim);

        var put = await PutFilamentAsync(attacker,
        [
            Adjustment(own, victim.Filament.Id, 900_000, "own"),
            Adjustment(Guid.Empty, victim.Filament.Id, 800_000, "new"),
        ]);

        Assert.True(put.IsSuccessStatusCode, $"PUT returned {(int)put.StatusCode}: {await put.Content.ReadAsStringAsync(Ct)}");
        Assert.Empty(await AdjustmentsAsync(victim.Filament.Id));
        Assert.Equal(victimRemainingBefore, await RemainingAsync(victim));
        var rows = await AdjustmentsAsync(attacker.Filament.Id);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Id == own && r.AmountMg == 900_000);
    }

    // ---- Printers: PUT /api/Printers/{id}, LoadedFilaments ----

    [Fact]
    public async Task PutPrinter_WithAnotherUsersLoadedRowId_LeavesVictimsRow()
    {
        var victim = await SeedUserAsync();
        var attacker = await SeedUserAsync(loadSpool: false);
        Guid victimLoadedId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            victimLoadedId = await db.PrinterFilament.Where(pf => pf.PrinterId == victim.Printer.Id).Select(pf => pf.Id).SingleAsync(Ct);
        }

        var dto = new AddPrinterDTO
        {
            Id = attacker.Printer.Id,
            Name = attacker.Printer.Name,
            Make = attacker.Printer.Make,
            Model = attacker.Printer.Model,
            IsActive = true,
            LoadedFilaments = [new AddPrinterFilamentDto { Id = victimLoadedId, FilamentId = attacker.Filament.Id }],
        };
        var put = await SendAsync(attacker, HttpMethod.Put, $"/api/Printers/{attacker.Printer.Id}", dto);

        Assert.True(put.IsSuccessStatusCode, $"PUT returned {(int)put.StatusCode}: {await put.Content.ReadAsStringAsync(Ct)}");
        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<PrintLogContext>();
        var victimRow = await verifyDb.PrinterFilament.AsNoTracking().SingleAsync(pf => pf.Id == victimLoadedId, Ct);
        Assert.Equal(victim.Printer.Id, victimRow.PrinterId);
        Assert.Equal(victim.Filament.Id, victimRow.FilamentId);
        Assert.Null(victimRow.UnloadedDateTime);

        var attackerRow = await verifyDb.PrinterFilament.AsNoTracking().SingleAsync(pf => pf.PrinterId == attacker.Printer.Id, Ct);
        Assert.NotEqual(victimLoadedId, attackerRow.Id);
        Assert.Equal(attacker.Filament.Id, attackerRow.FilamentId);
    }

    // ---- Helpers ----

    private async Task AssertVictimRowUntouchedAsync(Guid rowId, long printId, Guid filamentId, int amountMg)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var row = await db.PrintFilament.AsNoTracking().SingleOrDefaultAsync(r => r.Id == rowId, Ct);
        Assert.NotNull(row);
        Assert.Equal(printId, row!.PrintId);
        Assert.Equal(filamentId, row.FilamentId);
        Assert.Equal(amountMg, row.AmountMg);
    }

    private async Task<List<PrintFilament>> UsageRowsAsync(long printId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.PrintFilament.AsNoTracking().Where(r => r.PrintId == printId).ToListAsync(Ct);
    }

    private async Task<FilamentAdjustment?> AdjustmentAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.FilamentAdjustments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, Ct);
    }

    private async Task<List<FilamentAdjustment>> AdjustmentsAsync(Guid filamentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.FilamentAdjustments.AsNoTracking().Where(a => a.FilamentId == filamentId).ToListAsync(Ct);
    }

    private async Task<long?> RemainingAsync(Owner owner)
    {
        var response = await SendAsync(owner, HttpMethod.Get, $"/api/Filaments/{owner.Filament.Id}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var remaining = doc.RootElement.GetProperty("filamentRemaining");
        return remaining.ValueKind == JsonValueKind.Null ? null : remaining.GetInt64();
    }

    private Task<HttpResponseMessage> PutPrintAsync(Owner owner, Print print, List<PutPrintFilamentSummaryDto> usage) =>
        SendAsync(owner, HttpMethod.Put, $"/api/Prints/{print.Id}", new PutPrintDetailDto
        {
            Id = print.Id,
            Title = print.Title,
            PrinterId = owner.Printer.Id,
            Status = print.Status,
            ViewStatus = print.ViewStatus,
            StartDate = print.StartDate,
            PrintTimeInSeconds = print.PrintTimeInSeconds,
            FilamentUsage = usage,
        });

    private Task<HttpResponseMessage> PutFilamentAsync(Owner owner, List<FilamentAdjustmentDto> adjustments)
    {
        var f = owner.Filament;
        return SendAsync(owner, HttpMethod.Put, $"/api/Filaments/{f.Id}", new FilamentDetailDto
        {
            Id = f.Id,
            DisplayName = f.DisplayName,
            Brand = f.Brand,
            MaterialType = f.MaterialType,
            MaterialCategoryNickname = f.MaterialCategoryNickname,
            ColorName = f.ColorName,
            ColorHex = "000000",
            DiameterMm = f.DiameterMm,
            InitialNominalWeightMg = f.InitialNominalWeightMg,
            MaterialDensityGramPerCubicCm = f.MaterialDensityGramPerCubicCm,
            IsActive = true,
            FilamentAdjustments = adjustments,
        });
    }

    private static FilamentAdjustmentDto Adjustment(Guid id, Guid filamentId, long amountMg, string? notes) => new()
    {
        Id = id,
        FilamentId = filamentId,
        Source = FilamentAdjustment.SourceMeasurement.Weight,
        AmountMg = amountMg,
        Notes = notes,
    };

    private async Task<HttpResponseMessage> SendAsync(Owner owner, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, owner.User.OAuthUserId);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }
        return await _client.SendAsync(request, Ct);
    }

    private async Task<Owner> SeedUserAsync(bool loadSpool = true)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = new User
        {
            OAuthUserId = $"auth0|child-rows-{Guid.NewGuid()}",
            DisplayName = "Child rows user",
            ViewStatus = User.ProfileViewStatus.Public,
            Email = $"child-rows-{Guid.NewGuid():N}@example.test",
            EmailVerified = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);

        var printer = new Printer { Name = "Child rows printer", Make = "Prusa", Model = "MK4", UserId = user.Id, IsActive = true };
        db.Printers.Add(printer);
        var filament = new Filament
        {
            Id = Guid.NewGuid(),
            Brand = "Polymaker",
            DisplayName = "Galaxy Black PLA",
            ColorName = "Black",
            MaterialType = "PLA",
            MaterialCategoryNickname = "filament",
            MaterialDensityGramPerCubicCm = 1.24,
            DiameterMm = 1.75,
            InitialNominalWeightMg = 1_000_000,
            Source = Filament.SourceMeasurement.Weight,
            IsActive = true,
            CreatedById = user.Id,
            UpdatedById = user.Id,
        };
        db.Filaments.Add(filament);
        await db.SaveChangesAsync(Ct);
        if (loadSpool)
        {
            db.PrinterFilament.Add(new PrinterFilament { PrinterId = printer.Id, FilamentId = filament.Id, LoadedDateTime = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        }
        return new Owner(user, printer, filament);
    }

    private async Task<Print> SeedPrintAsync(Owner owner, PrintViewStatus view, int amountMg, int? extraRowMg = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var rows = new List<PrintFilament> { UsageRow(owner, amountMg) };
        if (extraRowMg is { } extra)
        {
            rows.Add(UsageRow(owner, extra));
        }
        var print = new Print
        {
            Title = "Benchy",
            PrinterId = owner.Printer.Id,
            Status = PrintStatus.Success,
            ViewStatus = view,
            StartDate = new DateTimeOffset(2026, 10, 3, 14, 0, 0, TimeSpan.Zero),
            PrintTimeInSeconds = 5400,
            CreatedById = owner.User.Id,
            UpdatedById = owner.User.Id,
            FilamentUsage = rows,
        };
        db.Prints.Add(print);
        await db.SaveChangesAsync(Ct);
        return print;
    }

    private static PrintFilament UsageRow(Owner owner, int amountMg) => new()
    {
        FilamentId = owner.Filament.Id,
        AmountMg = amountMg,
        Source = PrintFilament.SourceMeasurement.Weight,
        EstimatedSource = PrintFilament.SourceMeasurement.Weight,
    };

    private async Task<Guid> SeedAdjustmentAsync(Owner owner, long amountMg, string notes)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var adjustment = new FilamentAdjustment
        {
            FilamentId = owner.Filament.Id,
            Source = FilamentAdjustment.SourceMeasurement.Weight,
            AmountMg = amountMg,
            Notes = notes,
            CreatedById = owner.User.Id,
            UpdatedById = owner.User.Id,
        };
        db.FilamentAdjustments.Add(adjustment);
        await db.SaveChangesAsync(Ct);
        return adjustment.Id;
    }
}
