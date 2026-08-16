using Microsoft.EntityFrameworkCore;
using PrintLogApi.Mcp;
using PrintLogApi.Models;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp;

public class UpdatePrinterServiceTests : IClassFixture<McpDataWebApplicationFactory>
{
    private readonly McpDataWebApplicationFactory _factory;
    public UpdatePrinterServiceTests(McpDataWebApplicationFactory factory) => _factory = factory;
    private static IPrinterService Svc(IServiceScope s) => s.ServiceProvider.GetRequiredService<IPrinterService>();

    /// <summary>
    /// A fully-populated printer of the caller's own, so a clear test has something to clear and
    /// a patch test has a prior value to overwrite. Each test gets its own.
    /// </summary>
    private async Task<long> SeedPrinter(IServiceScope scope, string name)
    {
        var ctx = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var printer = new Printer
        {
            Name = name,
            Make = "Bambu",
            Model = "X1C",
            Description = "original description",
            CategoryNickname = "FFF",
            UserId = IntegrationTestSeeder.TestUserId,
            IsActive = true,
            NozzleDiameter = 0.4,
            FilamentDiameter = 1.75,
            BeamDiameter = 0.05,
            BedWidthMm = 256,
            BedDepthMm = 257,
            BedHeightMm = 258,
            ScreenResolutionXPixels = 3840,
            ScreenResolutionYPixels = 2160,
            HasHeatedBed = true,
            HasHeatedChamber = true,
            WattageW = 350,
        };
        ctx.Printers.Add(printer);
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();
        return printer.Id;
    }

    private static Task<PrinterDetailResult> Update(
        IServiceScope s, long id, PrinterAttributesInput input, ISet<string>? clear = null) =>
        Svc(s).UpdatePrinterForMcp(IntegrationTestSeeder.TestUserId, id, input, clear, CancellationToken.None);

    [Fact]
    public async Task Update_ForeignPrinter_IsNotFound()
    {
        using var scope = _factory.Services.CreateScope();
        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => Update(scope, McpTestData.OtherPrinterId, new PrinterAttributesInput { Name = "Hijacked" }));
        Assert.Equal("not_found", ex.Code);
    }

    // Same code for a foreign id and a nonexistent one: any difference would make this an
    // existence oracle for other users' printers.
    [Fact]
    public async Task Update_MissingPrinter_IsNotFound()
    {
        using var scope = _factory.Services.CreateScope();
        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => Update(scope, 999_999, new PrinterAttributesInput { Name = "Ghost" }));
        Assert.Equal("not_found", ex.Code);
    }

    [Fact]
    public async Task Update_ForeignPrinter_ChangesNothing()
    {
        using var scope = _factory.Services.CreateScope();
        await Assert.ThrowsAsync<McpToolException>(
            () => Update(scope, McpTestData.OtherPrinterId, new PrinterAttributesInput { Name = "Hijacked" }));

        var ctx = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var stored = await ctx.Printers.AsNoTracking().SingleAsync(p => p.Id == McpTestData.OtherPrinterId);
        Assert.Equal("Other User Printer", stored.Name);
    }

    [Fact]
    public async Task Update_ChangesOnlyTheFieldsPassed()
    {
        using var scope = _factory.Services.CreateScope();
        var id = await SeedPrinter(scope, "Patch Target");

        var result = await Update(scope, id, new PrinterAttributesInput { Name = "Renamed" });

        Assert.Equal("Renamed", result.Name);
        Assert.Equal("Bambu", result.Make);
        Assert.Equal("X1C", result.Model);
        Assert.Equal("original description", result.Description);
        Assert.Equal(0.4, result.NozzleDiameterMm);
        Assert.Equal(256, result.BedWidthMm);
        Assert.True(result.HasHeatedBed);
        Assert.Equal(350, result.WattageW);
    }

    [Fact]
    public async Task Update_SetsEverySettableField()
    {
        using var scope = _factory.Services.CreateScope();
        var id = await SeedPrinter(scope, "Full Patch");

        var result = await Update(scope, id, new PrinterAttributesInput
        {
            Make = "Prusa",
            Model = "MK4",
            Name = "Renamed Fully",
            Description = "new description",
            CategoryNickname = "SLA",
            NozzleDiameterMm = 0.6,
            FilamentDiameterMm = 2.85,
            BeamDiameterMm = 0.08,
            BedWidthMm = 300,
            BedDepthMm = 301,
            BedHeightMm = 302,
            ScreenResolutionXPixels = 1920,
            ScreenResolutionYPixels = 1080,
            HasHeatedBed = false,
            HasHeatedChamber = false,
            WattageW = 240,
            IsActive = false,
        });

        Assert.Equal("Prusa", result.Make);
        Assert.Equal("MK4", result.Model);
        Assert.Equal("Renamed Fully", result.Name);
        Assert.Equal("new description", result.Description);
        Assert.Equal("SLA", result.CategoryNickname);
        Assert.Equal(0.6, result.NozzleDiameterMm);
        Assert.Equal(2.85, result.FilamentDiameterMm);
        Assert.Equal(0.08, result.BeamDiameterMm);
        Assert.Equal(300, result.BedWidthMm);
        Assert.Equal(301, result.BedDepthMm);
        Assert.Equal(302, result.BedHeightMm);
        Assert.Equal(1920, result.ScreenResolutionXPixels);
        Assert.Equal(1080, result.ScreenResolutionYPixels);
        Assert.False(result.HasHeatedBed);
        Assert.False(result.HasHeatedChamber);
        Assert.Equal(240, result.WattageW);
        Assert.False(result.IsActive);
    }

    [Fact]
    public async Task Update_ClearsEveryClearableField()
    {
        using var scope = _factory.Services.CreateScope();
        var id = await SeedPrinter(scope, "Clear Target");

        var result = await Update(scope, id, new PrinterAttributesInput(),
            new HashSet<string>(McpPrinterValidation.ClearableFields));

        Assert.Null(result.Description);
        Assert.Null(result.NozzleDiameterMm);
        Assert.Null(result.FilamentDiameterMm);
        Assert.Null(result.BeamDiameterMm);
        Assert.Null(result.BedWidthMm);
        Assert.Null(result.BedDepthMm);
        Assert.Null(result.BedHeightMm);
        Assert.Null(result.ScreenResolutionXPixels);
        Assert.Null(result.ScreenResolutionYPixels);
        Assert.Null(result.HasHeatedBed);
        Assert.Null(result.HasHeatedChamber);
        Assert.Null(result.WattageW);
        // Identity survives: it is not clearable.
        Assert.Equal("Bambu", result.Make);
        Assert.Equal("Clear Target", result.Name);
        Assert.Equal("FFF", result.CategoryNickname);
        Assert.True(result.IsActive);
    }

    // Setting and clearing the same field is a contradiction. Guessing which one the caller
    // meant would make one of the two arguments silently ignored.
    [Fact]
    public async Task Update_SetAndClearSameField_IsRejected()
    {
        using var scope = _factory.Services.CreateScope();
        var id = await SeedPrinter(scope, "Collision");
        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => Update(scope, id, new PrinterAttributesInput { Description = "x" },
                new HashSet<string> { "description" }));
        Assert.Equal("invalid_arguments", ex.Code);
    }

    [Theory]
    [InlineData("make")]
    [InlineData("name")]
    [InlineData("isActive")]
    [InlineData("categoryNickname")]
    public async Task Update_ClearingANonClearableField_IsRejected(string field)
    {
        using var scope = _factory.Services.CreateScope();
        var id = await SeedPrinter(scope, $"Non Clearable {field}");
        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => Update(scope, id, new PrinterAttributesInput(), new HashSet<string> { field }));
        Assert.Equal("invalid_arguments", ex.Code);
    }

    [Fact]
    public async Task Update_UnknownCategory_IsRejected()
    {
        using var scope = _factory.Services.CreateScope();
        var id = await SeedPrinter(scope, "Bad Category Patch");
        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => Update(scope, id, new PrinterAttributesInput { CategoryNickname = "NOT-A-CATEGORY" }));
        Assert.Equal("invalid_arguments", ex.Code);
    }

    [Fact]
    public async Task Update_OmittedCategory_LeavesItUnchanged()
    {
        using var scope = _factory.Services.CreateScope();
        var id = await SeedPrinter(scope, "Category Untouched");
        var result = await Update(scope, id, new PrinterAttributesInput { Name = "Still FFF" });
        Assert.Equal("FFF", result.CategoryNickname);
    }

    // A legacy row can hold a null category (the column is nullable). An update that never
    // mentions the category must not quietly repair it — that would be an edit the caller did
    // not ask for.
    [Fact]
    public async Task Update_LeavesALegacyNullCategoryAlone()
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var printer = new Printer
        {
            Name = "Legacy Category",
            Make = "Old",
            Model = "Timer",
            UserId = IntegrationTestSeeder.TestUserId,
            IsActive = true,
        };
        ctx.Printers.Add(printer);
        await ctx.SaveChangesAsync();

        // The null CANNOT be seeded by the insert above: CategoryNickname carries a store default
        // of "FFF" (PrintLogContext.cs:417-419), so EF omits the column and the database fills it
        // in. Only an explicit UPDATE reaches the state a pre-default legacy row is actually in.
        await ctx.Printers.Where(p => p.Id == printer.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CategoryNickname, (string?)null));
        ctx.ChangeTracker.Clear();

        var result = await Update(scope, printer.Id, new PrinterAttributesInput { Name = "Still Legacy" });
        Assert.Null(result.CategoryNickname);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Update_BlankIdentityField_IsRejected(string blank)
    {
        using var scope = _factory.Services.CreateScope();
        var id = await SeedPrinter(scope, $"Blank {blank.Length}");
        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => Update(scope, id, new PrinterAttributesInput { Name = blank }));
        Assert.Equal("invalid_arguments", ex.Code);
    }

    [Fact]
    public async Task Update_NegativeNumeric_IsRejected()
    {
        using var scope = _factory.Services.CreateScope();
        var id = await SeedPrinter(scope, "Negative Patch");
        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => Update(scope, id, new PrinterAttributesInput { BedWidthMm = -1 }));
        Assert.Equal("invalid_arguments", ex.Code);
    }

    // Validate-then-mutate, asserted from the outside: a rejected edit must leave the row exactly
    // as it was, even though the name in the same call was perfectly valid.
    [Fact]
    public async Task Update_RejectedPatch_LeavesThePrinterUntouched()
    {
        using var scope = _factory.Services.CreateScope();
        var id = await SeedPrinter(scope, "Atomic Patch");

        await Assert.ThrowsAsync<McpToolException>(
            () => Update(scope, id, new PrinterAttributesInput { Name = "Should Not Stick", WattageW = -5 }));

        var ctx = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        ctx.ChangeTracker.Clear();
        var stored = await ctx.Printers.AsNoTracking().SingleAsync(p => p.Id == id);
        Assert.Equal("Atomic Patch", stored.Name);
        Assert.Equal(350, stored.WattageW);
    }

    // The invariant this whole surface exists to protect. SearchPrinterId carries one currently
    // loaded spool, one historical (unloaded) row, and one corrupt cross-owner row.
    [Fact]
    public async Task Update_DoesNotChangeLoadedFilamentState()
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var before = await ctx.Set<PrinterFilament>().AsNoTracking()
            .Where(pf => pf.PrinterId == McpTestData.SearchPrinterId)
            .Select(pf => new { pf.Id, pf.FilamentId, pf.LoadedDateTime, pf.UnloadedDateTime })
            .OrderBy(pf => pf.Id)
            .ToListAsync();
        ctx.ChangeTracker.Clear();

        await Update(scope, McpTestData.SearchPrinterId, new PrinterAttributesInput { Description = "edited" });

        // Stronger than "no PrinterFilament was Added/Modified/Deleted": the write path never
        // loads them at all, so there is nothing in the tracker that a later SaveChanges on this
        // context could flush.
        Assert.Empty(ctx.ChangeTracker.Entries<PrinterFilament>());

        var after = await ctx.Set<PrinterFilament>().AsNoTracking()
            .Where(pf => pf.PrinterId == McpTestData.SearchPrinterId)
            .Select(pf => new { pf.Id, pf.FilamentId, pf.LoadedDateTime, pf.UnloadedDateTime })
            .OrderBy(pf => pf.Id)
            .ToListAsync();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Update_StillReportsTheLoadedSpool()
    {
        using var scope = _factory.Services.CreateScope();
        var result = await Update(scope, McpTestData.SearchPrinterId,
            new PrinterAttributesInput { Description = "edited again" });

        // Long PLA is the one currently-loaded, owned spool on the search fixture printer.
        var loaded = Assert.Single(result.LoadedFilaments);
        Assert.Equal("Long PLA", loaded.Name);
    }

    [Fact]
    public async Task Update_TrimsStrings()
    {
        using var scope = _factory.Services.CreateScope();
        var id = await SeedPrinter(scope, "Trim Patch");
        var result = await Update(scope, id, new PrinterAttributesInput { Name = "  Trimmed  " });
        Assert.Equal("Trimmed", result.Name);
    }

    [Fact]
    public async Task Update_BumpsCacheVersion()
    {
        var cache = _factory.Services.GetRequiredService<ICacheVersionService>();
        using var scope = _factory.Services.CreateScope();
        var id = await SeedPrinter(scope, "Cache Patch");
        var before = cache.GetUserCacheVersion(IntegrationTestSeeder.TestUserId);
        await Update(scope, id, new PrinterAttributesInput { Name = "Cache Patched" });
        Assert.NotEqual(before, cache.GetUserCacheVersion(IntegrationTestSeeder.TestUserId));
    }
}
