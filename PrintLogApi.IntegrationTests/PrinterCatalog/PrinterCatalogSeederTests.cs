using Microsoft.EntityFrameworkCore;
using PrintLogApi.PrinterCatalog;
using PrintLogApi.PrinterCatalog.Generation;
using Xunit;

namespace PrintLogApi.IntegrationTests.PrinterCatalog;

public class PrinterCatalogSeederTests
{
    private static CatalogModel Model(string id, double bedWidth = 256, IReadOnlyList<string>? aliases = null) => new()
    {
        Id = id,
        SourceKeys = [$"orca:{id}"],
        Make = "Acme",
        Model = id.ToUpperInvariant(),
        Aliases = aliases,
        Technology = "FFF",
        BedWidthMm = bedWidth,
        BedDepthMm = 256,
        BedHeightMm = 250,
        NozzleOptionsMm = [0.4, 0.6],
        DefaultNozzleMm = 0.4,
        FilamentDiameterMm = 1.75,
        HasHeatedBed = true,
        ExtruderCount = 1,
        Sources = new Dictionary<string, string> { ["bedWidthMm"] = "orca" },
    };

    private static CatalogFile File(string version, IReadOnlyList<CatalogModel> models, IReadOnlyList<ReservedId>? reserved = null) => new()
    {
        Version = version,
        Models = models,
        Reserved = reserved ?? [],
    };

    private static ReservedId Merged(string id, string into) => new() { Id = id, MergedInto = into };

    [Fact]
    public async Task FirstSeed_InsertsEveryModel_AndRecordsTheVersion()
    {
        await using var host = new PrinterCatalogTestHost();

        var result = await host.SeedAsync(File("v1", [Model("a", aliases: ["Alpha"]), Model("b")]));

        Assert.Equal(new SeedResult(2, 0, 0, 0, 0), result);
        var rows = await host.RowsAsync();
        Assert.Equal(["a", "b"], rows.Keys.Order());
        var a = rows["a"];
        Assert.Equal("Acme", a.Make);
        Assert.Equal("A", a.Model);
        Assert.Equal("[\"Alpha\"]", a.AliasesJson);
        Assert.Equal("[0.4,0.6]", a.NozzleOptionsJson);
        Assert.Equal(256, a.BedWidthMm);
        Assert.Equal(1.75, a.FilamentDiameterMm);
        Assert.False(a.Hidden);
        Assert.False(a.Retired);
        Assert.Equal(PrinterCatalogTestHost.Start, a.UpdatedAt);
        Assert.Equal("[]", rows["b"].AliasesJson);
        Assert.Equal("v1", await host.MarkerAsync());
    }

    [Fact]
    public async Task SecondRun_WithTheSameVersion_IsAllUnchanged()
    {
        await using var host = new PrinterCatalogTestHost();
        var file = File("v1", [Model("a"), Model("b")]);
        await host.SeedAsync(file);
        host.Time.SetUtcNow(PrinterCatalogTestHost.Start.AddDays(1));

        var result = await host.SeedAsync(file);

        Assert.Equal(new SeedResult(0, 0, 0, 2, 0), result);
        Assert.All((await host.RowsAsync()).Values, r => Assert.Equal(PrinterCatalogTestHost.Start, r.UpdatedAt));
    }

    [Fact]
    public async Task Rerun_WithoutTheMarker_ComparesEveryRow_AndFindsNothingChanged()
    {
        await using var host = new PrinterCatalogTestHost();
        var file = File("v1", [Model("a", aliases: ["Alpha"]), Model("b")]);
        await host.SeedAsync(file);
        await using (var scope = host.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<PrintLogContext>().PrinterCatalogVersions
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        var result = await host.SeedAsync(file);

        Assert.Equal(new SeedResult(0, 0, 0, 2, 0), result);
        Assert.Equal("v1", await host.MarkerAsync());
    }

    [Fact]
    public async Task ChangedField_Updates_AndKeepsOwnerCount()
    {
        await using var host = new PrinterCatalogTestHost();
        await host.SeedAsync(File("v1", [Model("a"), Model("b")]));
        await using (var scope = host.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<PrintLogContext>().PrinterModels
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.OwnerCount, 7), TestContext.Current.CancellationToken);
        }
        var later = PrinterCatalogTestHost.Start.AddDays(30);
        host.Time.SetUtcNow(later);

        var result = await host.SeedAsync(File("v2", [Model("a", bedWidth: 300), Model("b")]));

        Assert.Equal(new SeedResult(0, 1, 0, 1, 0), result);
        var rows = await host.RowsAsync();
        Assert.Equal(300, rows["a"].BedWidthMm);
        Assert.Equal(later, rows["a"].UpdatedAt);
        Assert.Equal(PrinterCatalogTestHost.Start, rows["b"].UpdatedAt);
        Assert.All(rows.Values, r => Assert.Equal(7, r.OwnerCount));
        Assert.Equal("v2", await host.MarkerAsync());
    }

    [Fact]
    public async Task ModelRemovedFromTheFile_IsRetired_NotDeleted_AndReturnsWhenItComesBack()
    {
        await using var host = new PrinterCatalogTestHost();
        await host.SeedAsync(File("v1", [Model("a"), Model("b")]));

        var removed = await host.SeedAsync(File("v2", [Model("a")], [new ReservedId { Id = "b" }]));

        Assert.Equal(new SeedResult(0, 0, 1, 1, 0), removed);
        var b = (await host.RowsAsync())["b"];
        Assert.True(b.Retired);
        Assert.False(b.Hidden);

        var back = await host.SeedAsync(File("v3", [Model("a"), Model("b")]));

        Assert.Equal(new SeedResult(0, 1, 0, 1, 0), back);
        Assert.False((await host.RowsAsync())["b"].Retired);
    }

    [Fact]
    public async Task AlreadyRetiredRow_IsNotCountedAgain()
    {
        await using var host = new PrinterCatalogTestHost();
        await host.SeedAsync(File("v1", [Model("a"), Model("b")]));
        await host.SeedAsync(File("v2", [Model("a")]));

        var result = await host.SeedAsync(File("v3", [Model("a", bedWidth: 1)]));

        Assert.Equal(new SeedResult(0, 1, 0, 0, 0), result);
        Assert.True((await host.RowsAsync())["b"].Retired);
    }

    [Fact]
    public async Task FailedSeed_LeavesTheTableAndTheMarkerUnchanged()
    {
        await using var host = new PrinterCatalogTestHost();
        await host.SeedAsync(File("v1", [Model("a"), Model("b")]));

        // A null version fails at the marker write, after the model rows were saved: only a
        // transaction around the whole pass keeps them out.
        await Assert.ThrowsAnyAsync<Exception>(() => host.SeedAsync(File(null!, [Model("a", bedWidth: 999), Model("c")])));

        var rows = await host.RowsAsync();
        Assert.Equal(["a", "b"], rows.Keys.Order());
        Assert.Equal(256, rows["a"].BedWidthMm);
        Assert.False(rows["b"].Retired);
        Assert.Equal("v1", await host.MarkerAsync());
    }

    [Fact]
    public async Task DuplicateIdsInTheFile_FailWithoutWriting()
    {
        await using var host = new PrinterCatalogTestHost();

        await Assert.ThrowsAsync<InvalidDataException>(() => host.SeedAsync(File("v1", [Model("a"), Model("a")])));

        Assert.Empty(await host.RowsAsync());
        Assert.Null(await host.MarkerAsync());
    }

    [Fact]
    public async Task MergedIds_RepointPrinters_ToTheEndOfTheChain()
    {
        await using var host = new PrinterCatalogTestHost();
        await host.SeedAsync(File("v1", [Model("a"), Model("b"), Model("c")]));
        var printers = await host.AddPrintersAsync("a", "b", "c");

        var result = await host.SeedAsync(File("v2", [Model("c")], [Merged("a", "b"), Merged("b", "c")]));

        Assert.Equal(2, result.Repointed);
        Assert.Equal(["c", "c", "c"], await host.PrinterLinksAsync(printers));
    }

    [Fact]
    public async Task MergedCycle_LeavesTheLinks()
    {
        await using var host = new PrinterCatalogTestHost();
        await host.SeedAsync(File("v1", [Model("x"), Model("y"), Model("z")]));
        var printers = await host.AddPrintersAsync("x", "y");

        var result = await host.SeedAsync(File("v2", [Model("z")], [Merged("x", "y"), Merged("y", "x")]));

        Assert.Equal(0, result.Repointed);
        Assert.Equal(["x", "y"], await host.PrinterLinksAsync(printers));
    }

    [Fact]
    public async Task MergedIntoAReservedId_LeavesTheLinks()
    {
        await using var host = new PrinterCatalogTestHost();
        await host.SeedAsync(File("v1", [Model("p"), Model("q"), Model("r")]));
        var printers = await host.AddPrintersAsync("p");

        var result = await host.SeedAsync(File("v2", [Model("r")], [Merged("p", "q"), new ReservedId { Id = "q" }]));

        Assert.Equal(0, result.Repointed);
        Assert.Equal(["p"], await host.PrinterLinksAsync(printers));
    }

    [Fact]
    public async Task Repointing_RunsOnlyWhenTheVersionChanged()
    {
        await using var host = new PrinterCatalogTestHost();
        await host.SeedAsync(File("v1", [Model("a"), Model("b")]));
        var file = File("v2", [Model("b")], [Merged("a", "b")]);
        await host.SeedAsync(file);
        var printers = await host.AddPrintersAsync("a");

        var result = await host.SeedAsync(file);

        Assert.Equal(0, result.Repointed);
        Assert.Equal(["a"], await host.PrinterLinksAsync(printers));
    }

    [Fact]
    public async Task RunOnce_WhenTheCatalogCannotBeRead_LogsKeepsTheTable_AndStillBuildsTheIndex()
    {
        await using var host = new PrinterCatalogTestHost();
        await host.SeedAsync(File("v1", [Model("a")]));

        await host.Seeder(() => throw new InvalidDataException("broken file")).RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["a"], (await host.RowsAsync()).Keys);
        Assert.Equal("v1", await host.MarkerAsync());
        Assert.NotNull(host.Index.Get("a"));
    }

    [Fact]
    public async Task RunOnce_SeedsTheEmbeddedCatalog_AndRebuildsTheIndex()
    {
        await using var host = new PrinterCatalogTestHost();
        var embedded = PrinterCatalogFileLoader.LoadEmbedded();

        await host.Seeder().RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(embedded.Models.Count, (await host.RowsAsync()).Count);
        Assert.Equal(embedded.Version, await host.MarkerAsync());
        Assert.Equal("bambu-lab-p1s", host.Index.Search("p1s", 1).Single().Id);
    }
}
