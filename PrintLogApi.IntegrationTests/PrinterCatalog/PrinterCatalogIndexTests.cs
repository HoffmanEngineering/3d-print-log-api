using Microsoft.Extensions.Options;
using PrintLogApi.Models;
using PrintLogApi.PrinterCatalog;
using Xunit;

namespace PrintLogApi.IntegrationTests.PrinterCatalog;

public class PrinterCatalogIndexTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The real embedded catalog, as the seeder would store it.</summary>
    private static readonly Lazy<PrinterCatalogIndex> Real = new(() =>
    {
        var index = NewIndex();
        index.Rebuild(PrinterCatalogFileLoader.LoadEmbedded().Models.Select(m => PrinterCatalogSeeder.ToEntity(m, Now)));
        return index;
    });

    private static PrinterCatalogIndex NewIndex() => new(Options.Create(new PrinterCatalogOptions()));

    private static PrinterModel Row(string id, string make, string model, int owners = 0, int? featured = null,
        bool hidden = false, bool retired = false, string aliasesJson = "[]") => new()
        {
            Id = id,
            Make = make,
            Model = model,
            AliasesJson = aliasesJson,
            Technology = "FFF",
            NozzleOptionsJson = "[]",
            SourcesJson = "{}",
            OwnerCount = owners,
            Featured = featured,
            Hidden = hidden,
            Retired = retired,
            UpdatedAt = Now,
        };

    private static string First(string query) => Real.Value.Search(query, 10)[0].Id;

    [Theory]
    [InlineData("x1c", "bambu-lab-x1-carbon")]
    [InlineData("X1C", "bambu-lab-x1-carbon")]
    [InlineData("k1 max", "creality-k1-max")]
    [InlineData("bambo p1s", "bambu-lab-p1s")]
    [InlineData("p1s", "bambu-lab-p1s")]
    [InlineData("bmabu p1s", "bambu-lab-p1s")]
    public void Search_PutsTheExpectedModelFirst(string query, string expected) => Assert.Equal(expected, First(query));

    [Fact]
    public void Search_Mk4_GivesAPrusaMk4OrMk4sFirst() => Assert.Contains(First("mk4"), new[] { "prusa-mk4", "prusa-mk4s" });

    [Fact]
    public void Search_Ender3V3Se_IgnoresSpacingAndPunctuation()
    {
        Assert.Equal("creality-ender-3-v3-se", First("ender 3 v3 se"));
        Assert.Equal("creality-ender-3-v3-se", First("ender3v3se"));
        Assert.Equal("creality-ender-3-v3-se", First("Ender-3 V3 SE"));
    }

    [Fact]
    public void Search_Voron24_FindsAVoron24() => Assert.StartsWith("voron-2-4-", First("voron 2.4"));

    [Fact]
    public void Search_RespectsTheLimit() => Assert.Equal(3, Real.Value.Search("creality", 3).Count);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Search_WithAnEmptyQuery_IsThePopularList(string? query) =>
        Assert.Equal(Real.Value.Popular(8, 5).Select(m => m.Id), Real.Value.Search(query, 8).Select(m => m.Id));

    [Fact]
    public void Popular_WithNoCounts_IsTheFeaturedOrder()
    {
        var popular = Real.Value.Popular(8, 5);

        Assert.Equal(8, popular.Count);
        Assert.Equal(["bambu-lab-a1", "bambu-lab-p1s", "bambu-lab-a1-mini", "bambu-lab-x1-carbon"], popular.Take(4).Select(m => m.Id));
    }

    [Fact]
    public void Popular_RanksModelsAtTheThresholdByCount_ThenFillsWithFeatured()
    {
        var index = NewIndex();
        index.Rebuild([
            Row("f1", "Acme", "F1", featured: 1),
            Row("f2", "Acme", "F2", featured: 2, owners: 9),
            Row("f3", "Acme", "F3", featured: 3),
            Row("big", "Acme", "Big", owners: 12),
            Row("mid", "Acme", "Mid", owners: 5),
            Row("low", "Acme", "Low", owners: 4),
        ]);

        Assert.Equal(["big", "f2", "mid", "f1", "f3"], index.Popular(10, 5).Select(m => m.Id));
        Assert.Equal(["big", "f2"], index.Popular(2, 5).Select(m => m.Id));
    }

    [Fact]
    public void HiddenAndRetiredModels_AreNeverOffered_ButStillResolveById()
    {
        var index = NewIndex();
        index.Rebuild([
            Row("shown", "Acme", "Widget", featured: 3),
            Row("hidden", "Acme", "Widget Hidden", owners: 50, featured: 1, hidden: true),
            Row("retired", "Acme", "Widget Retired", owners: 50, featured: 2, retired: true),
        ]);

        Assert.Equal(["shown"], index.Search("widget", 10).Select(m => m.Id));
        Assert.DoesNotContain(index.Search("widget hidden", 10), m => m.Id != "shown");
        Assert.Equal(["shown"], index.Popular(10, 5).Select(m => m.Id));
        Assert.Null(index.Match("Acme Widget Retired"));
        Assert.True(index.Get("hidden")!.Hidden);
        Assert.True(index.Get("retired")!.Retired);
        Assert.Null(index.Get("missing"));
    }

    [Fact]
    public void WithinATier_HigherOwnerCountRanksFirst_ThenFeatured()
    {
        var index = NewIndex();
        index.Rebuild([
            Row("few", "Acme", "Gadget One", owners: 1),
            Row("many", "Acme", "Gadget Two", owners: 9),
            Row("featured", "Acme", "Gadget Three", owners: 1, featured: 4),
        ]);

        Assert.Equal(["many", "featured", "few"], index.Search("gadget", 10).Select(m => m.Id));
    }

    [Fact]
    public void AHigherTier_BeatsAHigherOwnerCount()
    {
        var index = NewIndex();
        index.Rebuild([
            Row("contains", "Acme", "Super Gizmo", owners: 100),
            Row("prefix", "Acme", "Gizmo", owners: 0),
        ]);

        Assert.Equal(["prefix", "contains"], index.Search("gizmo", 10).Select(m => m.Id));
    }

    [Fact]
    public void Version_ChangesWhenTheModelsChange()
    {
        var index = NewIndex();
        index.Rebuild([Row("a", "Acme", "A")]);
        var first = index.Version;
        index.Rebuild([Row("a", "Acme", "A")]);
        Assert.Equal(first, index.Version);

        index.Rebuild([Row("a", "Acme", "A", owners: 6)]);

        Assert.NotEqual(first, index.Version);
    }

    [Fact]
    public async Task Rebuild_DuringConcurrentReads_NeverBreaksAReader()
    {
        var index = NewIndex();
        var rows = PrinterCatalogFileLoader.LoadEmbedded().Models.Select(m => PrinterCatalogSeeder.ToEntity(m, Now)).ToList();
        index.Rebuild(rows);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                Assert.NotEmpty(index.Search("p1s", 5));
                Assert.NotNull(index.Get("bambu-lab-p1s"));
            }
        }, TestContext.Current.CancellationToken)).ToList();
        while (!stop.IsCancellationRequested)
        {
            index.Rebuild(rows);
        }

        await Task.WhenAll(readers);
    }

    [Fact]
    public void Match_BambuSlicerName_IsExact() =>
        Assert.Equal(new CatalogMatch("bambu-lab-x1-carbon", CatalogMatch.Exact), Real.Value.Match("Bambu Lab X1 Carbon 0.4 nozzle"));

    [Fact]
    public void Match_PrusaSlicerName_FindsThePrusaModel()
    {
        var match = Real.Value.Match("Original Prusa MK4S MMU3");

        Assert.NotNull(match);
        Assert.StartsWith("prusa-mk4s", match.Id);
        Assert.Equal("Prusa", Real.Value.Get(match.Id)!.Make);
    }

    [Fact]
    public void Match_CleansANozzleSuffixInParentheses() =>
        Assert.Equal(new CatalogMatch("creality-ender-3-v3-se", CatalogMatch.Exact), Real.Value.Match("Creality Ender-3 V3 SE (0.6 nozzle)"));

    [Theory]
    [InlineData("Bambu Lab P1S (Copy)")]
    [InlineData("Bambu Lab P1S 0.4 nozzle - Copy")]
    [InlineData("Bambu Lab P1S - 3DPrintLog")]
    [InlineData("\"Bambu Lab P1S\"")]
    [InlineData("\"Bambu Lab P1S")]
    [InlineData("Bambu Lab P1S\"")]
    public void Match_CleansSlicerDecorations(string name) =>
        Assert.Equal(new CatalogMatch("bambu-lab-p1s", CatalogMatch.Exact), Real.Value.Match(name));

    [Fact]
    public void Match_AnAliasName_IsAlias() =>
        Assert.Equal(new CatalogMatch("bambu-lab-x1-carbon", CatalogMatch.Alias), Real.Value.Match("Bambu Lab X1C"));

    [Fact]
    public void Match_ATypo_IsFuzzy() =>
        Assert.Equal(new CatalogMatch("bambu-lab-x1-carbon", CatalogMatch.Fuzzy), Real.Value.Match("Bambu Lab X1 Carbn"));

    [Theory]
    [InlineData("Totally Unknown 9000")]
    [InlineData("Prusa")]
    [InlineData("")]
    [InlineData("0.4 nozzle")]
    public void Match_UnknownOrAmbiguous_IsNull(string name) => Assert.Null(Real.Value.Match(name));

    [Fact]
    public void AShortToken_IsNeverFuzzed() =>
        Assert.DoesNotContain("bambu-lab-x1-carbon", Real.Value.Search("x1d", 50).Select(m => m.Id));

    [Fact]
    public void TheIndex_HoldsCopies_NotTheRowsItWasBuiltFrom()
    {
        var index = NewIndex();
        var row = Row("a", "Acme", "A", owners: 3);
        index.Rebuild([row]);
        row.OwnerCount = 99;

        var held = index.Get("a")!;
        Assert.NotSame(row, held);
        Assert.Equal(3, held.OwnerCount);
    }

    [Fact]
    public void IsReady_IsFalseUntilTheFirstRebuild()
    {
        var index = NewIndex();
        Assert.False(index.IsReady);

        index.Rebuild([]);

        Assert.True(index.IsReady);
    }
}
