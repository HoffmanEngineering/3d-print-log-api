using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PrintLogApi.PrinterCatalog;
using PrintLogApi.PrinterCatalog.Generation;
using Xunit;

namespace PrintLogApi.IntegrationTests.PrinterCatalog;

/// <summary>
/// Hand-checked specs for the popular models (spec §5.4), against the committed, embedded
/// <c>printer-catalog.json</c>. A monthly refresh that changes one of these fails here until the
/// golden list or an override is updated on purpose. Each row was checked against the
/// manufacturer's published spec page, cited beside it. Where the slicer profile disagreed, an
/// override in <c>catalog-overrides.json</c> carries the manufacturer's value and a note.
/// </summary>
public partial class PrinterCatalogGoldenTests
{
    private static readonly CatalogFile Catalog = PrinterCatalogFileLoader.LoadEmbedded();

    public sealed record Golden(
        string Id, double Width, double Depth, double Height, double Filament, bool HeatedBed, int? Slots);

    public static TheoryData<Golden> GoldenModels =>
    [
        // https://bambulab.com/en/a1/tech-specs (256 x 256 x 256 mm, AMS lite 4 slots)
        new Golden("bambu-lab-a1", 256, 256, 256, 1.75, true, 4),
        // https://bambulab.com/en/a1-mini/tech-specs (180 x 180 x 180 mm, AMS lite 4 slots)
        new Golden("bambu-lab-a1-mini", 180, 180, 180, 1.75, true, 4),
        // https://bambulab.com/en/p1/tech-specs (P1S 256 x 256 x 256 mm, AMS 4 slots)
        new Golden("bambu-lab-p1s", 256, 256, 256, 1.75, true, 4),
        // https://bambulab.com/en/p2s/tech-specs (256 x 256 x 256 mm, AMS 2 Pro 4 slots)
        new Golden("bambu-lab-p2s", 256, 256, 256, 1.75, true, 4),
        // https://bambulab.com/en/x1/tech-specs (X1 Carbon 256 x 256 x 256 mm, AMS 4 slots)
        new Golden("bambu-lab-x1-carbon", 256, 256, 256, 1.75, true, 4),
        // https://www.prusa3d.com/product/original-prusa-mk4s-3d-printer-5/ (250 x 210 x 220 mm)
        new Golden("prusa-mk4s", 250, 210, 220, 1.75, true, null),
        // https://www.prusa3d.com/product/original-prusa-mk4s-3d-printer-5/ (MMU3: 5 filaments)
        new Golden("prusa-mk4s-mmu3", 250, 210, 220, 1.75, true, 5),
        // https://www.prusa3d.com/product/prusa-core-one/ (250 x 220 x 270 mm)
        new Golden("prusa-core-one", 250, 220, 270, 1.75, true, null),
        // https://www.prusa3d.com/en/product/original-prusa-mini-semi-assembled-3d-printer-9 (MINI+ 180 x 180 x 180 mm)
        new Golden("prusa-mini", 180, 180, 180, 1.75, true, null),
        // https://www.creality.com/products/creality-ender-3-v3-se (220 x 220 x 250 mm)
        new Golden("creality-ender-3-v3-se", 220, 220, 250, 1.75, true, null),
        // https://www.creality.com/products/creality-ender-3-v3 (220 x 220 x 250 mm)
        new Golden("creality-ender-3-v3", 220, 220, 250, 1.75, true, null),
        // https://www.creality.com/products/k1c-carbon-3d-printer (220 x 220 x 250 mm)
        new Golden("creality-k1c", 220, 220, 250, 1.75, true, null),
        // https://www.creality.com/products/creality-k1-max-3d-printer (300 x 300 x 300 mm)
        new Golden("creality-k1-max", 300, 300, 300, 1.75, true, null),
        // https://www.creality.com/products/creality-k2-plus-cfs-combo (350 x 350 x 350 mm, CFS 4 slots)
        new Golden("creality-k2-plus", 350, 350, 350, 1.75, true, 4),
        // https://us.elegoo.com/products/elegoo-neptune-4-fdm-3d-printer (225 x 225 x 265 mm)
        new Golden("elegoo-neptune-4", 225, 225, 265, 1.75, true, null),
        // https://us.elegoo.com/products/elegoo-neptune-4-pro-fdm-3d-printer (225 x 225 x 265 mm)
        new Golden("elegoo-neptune-4-pro", 225, 225, 265, 1.75, true, null),
        // https://store.anycubic.com/products/anycubic-kobra-3 (250 x 250 x 260 mm, ACE Pro 4 slots in the Combo)
        new Golden("anycubic-kobra-3", 250, 250, 260, 1.75, true, 4),
        // https://sovol3d.com/products/sovol-sv06-direct-drive-3d-printer (220 x 220 x 250 mm)
        new Golden("sovol-sv06", 220, 220, 250, 1.75, true, null),
        // https://github.com/VoronDesign/Voron-2/blob/Voron2.4/firmware/klipper_configurations/Octopus/Voron2_Octopus_Config.cfg
        // (350 build: X/Y position_max 350, Z position_max 310)
        new Golden("voron-2-4-350", 350, 350, 310, 1.75, true, null),
        // https://www.snapmaker.com/snapmaker-u1/specs (270 x 270 x 270 mm, 4 toolheads)
        new Golden("snapmaker-u1", 270, 270, 270, 1.75, true, 4),
        // https://qidi3d.com/products/q1-pro-3d-printer (245 x 245 x 240 mm)
        new Golden("qidi-q1-pro", 245, 245, 240, 1.75, true, null),
        // https://qidi3d.com/blogs/news/qidi-3d-printer-comparison (305 x 305 x 280 mm)
        new Golden("qidi-x-plus-4", 305, 305, 280, 1.75, true, null),
        // https://flashforge.com/products/adventurer-5m-3d-printer (220 x 220 x 220 mm)
        new Golden("flashforge-adventurer-5m", 220, 220, 220, 1.75, true, null),
    ];

    [Theory]
    [MemberData(nameof(GoldenModels))]
    public void AGoldenModel_HasTheManufacturersSpecs(Golden expected)
    {
        var model = Assert.Single(Catalog.Models, m => m.Id == expected.Id);

        Assert.Equal("FFF", model.Technology);
        Assert.Equal(expected.Width, model.BedWidthMm);
        Assert.Equal(expected.Depth, model.BedDepthMm);
        Assert.Equal(expected.Height, model.BedHeightMm);
        Assert.Equal(expected.Filament, model.FilamentDiameterMm);
        Assert.Equal(expected.HeatedBed, model.HasHeatedBed);
        Assert.Equal(expected.Slots, model.DefaultSlotCount);
        Assert.Equal(0.4, model.DefaultNozzleMm);
    }

    [Theory]
    [InlineData("bambu-lab-x1-carbon", "X1C")]
    [InlineData("prusa-mk4s", "MK4S")]
    [InlineData("prusa-mini", "MINI+")]
    [InlineData("creality-ender-3-v3-se", "Ender 3 V3 SE")]
    [InlineData("creality-k1c", "K1C")]
    [InlineData("qidi-x-plus-4", "Plus4")]
    [InlineData("flashforge-adventurer-5m", "AD5M")]
    [InlineData("voron-2-4-350", "2.4 350")]
    public void AGoldenModel_IsFoundByTheNameOwnersUse(string id, string alias)
    {
        var model = Assert.Single(Catalog.Models, m => m.Id == id);
        var names = (model.Aliases ?? []).Append(model.Model).Select(CatalogNormalizer.Normalize);

        Assert.Contains(CatalogNormalizer.Normalize(alias), names);
    }

    /// <summary>The UI (K3) and the Spoolman rule (C7) use this literal id.</summary>
    [Fact]
    public void SnapmakerU1_HasItsFixedId_AndFourSlots()
    {
        var u1 = Assert.Single(Catalog.Models, m => m.Id == "snapmaker-u1");

        Assert.Equal("Snapmaker", u1.Make);
        Assert.Equal(4, u1.DefaultSlotCount);
    }

    [Fact]
    public void EveryId_IsUnique_AndASlug()
    {
        var ids = Catalog.Models.Select(m => m.Id).Concat(Catalog.Reserved.Select(r => r.Id)).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.Matches(Slug(), id));
        Assert.All(ids, id => Assert.True(id.Length <= 100, id));
    }

    /// <summary><c>PrinterModel.Make</c> and <c>Model</c> are 50 characters (C1).</summary>
    [Fact]
    public void EveryName_FitsThePrinterModelColumns()
    {
        Assert.All(Catalog.Models, m =>
        {
            Assert.InRange(m.Make.Length, 1, 50);
            Assert.InRange(m.Model.Length, 1, 50);
        });
    }

    [Fact]
    public void Sources_ListBothRepos_WithACommitAndALicense()
    {
        Assert.Collection(
            Catalog.Sources.OrderBy(s => s.Repo, StringComparer.Ordinal),
            s => AssertSource(s, "SoftFever/OrcaSlicer", "AGPL-3.0"),
            s => AssertSource(s, "Ultimaker/Cura", "LGPL-3.0"));
        Assert.Equal(CatalogFile.AttributionText, Catalog.Attribution);
    }

    [Fact]
    public void TheFile_IsVersionedByItsModels()
    {
        Assert.Equal(CatalogFile.ComputeVersion(Catalog.Models), Catalog.Version);
    }

    /// <summary>
    /// The first published file was generated with no previous file, so nothing is retired or
    /// merged yet: the only reserved ids are the ones an override hides.
    /// </summary>
    [Fact]
    public void TheFirstPublishedFile_HasNoMergedIds()
    {
        Assert.All(Catalog.Reserved, r => Assert.Null(r.MergedInto));
    }

    // --- Sanity rules over every model (fix round 1) ---

    private static readonly JsonObject Overrides = JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "printer-catalog-data", "catalog-overrides.json")))!.AsObject();

    private static bool OverrideExplains(string id, string field) =>
        Overrides[id] is JsonObject entry
        && entry.ContainsKey(field)
        && entry["note"]?.GetValue<string>() is { Length: > 0 };

    /// <summary>Every Bambu Lab printer takes an AMS (or AMS lite), 4 slots.</summary>
    [Fact]
    public void EveryBambuLabModel_HasTheAmsFourSlots()
    {
        var bambu = Catalog.Models.Where(m => m.Make == "Bambu Lab").ToList();

        Assert.NotEmpty(bambu);
        Assert.All(bambu, m => Assert.True(m.DefaultSlotCount == 4, $"{m.Id}: {m.DefaultSlotCount}"));
    }

    [Fact]
    public void AMultiExtruderModel_HasASlotPerExtruder_UnlessAnOverrideSaysWhy()
    {
        Assert.All(
            Catalog.Models.Where(m => m.ExtruderCount > 1),
            m => Assert.True(
                m.DefaultSlotCount >= m.ExtruderCount || OverrideExplains(m.Id, "defaultSlotCount"),
                $"{m.Id}: {m.ExtruderCount} extruders, {m.DefaultSlotCount?.ToString() ?? "no"} slots"));
    }

    /// <summary>The Combo policy: a model whose main SKU ships with a multi-material unit gets its slots.</summary>
    [Theory]
    [InlineData("bambu-lab-x2d", 4)] // https://bambulab.com/en/x2d (X2D Combo: AMS 2 Pro)
    [InlineData("flashforge-creator-5", 4)] // https://flashforge.com (4-toolhead toolchanger)
    [InlineData("flashforge-creator-5-pro", 4)]
    [InlineData("creality-k2", 4)] // K2 Combo: one CFS
    [InlineData("creality-k2-pro", 4)] // K2 Pro Combo: one CFS
    [InlineData("creality-hi", 4)] // https://www.creality.com/products/creality-hi-combo
    [InlineData("anycubic-kobra-s1", 4)] // https://store.anycubic.com/products/kobra-s1-combo
    [InlineData("elegoo-centauri-carbon-2", 4)] // https://www.elegoo.com/products/canvas-for-centauri-carbon-2-centauri-2-series
    public void AComboModel_HasItsUnitsSlots(string id, int slots)
    {
        Assert.Equal(slots, Assert.Single(Catalog.Models, m => m.Id == id).DefaultSlotCount);
    }

    [Fact]
    public void FilamentDiameter_IsUnknown175Or285()
    {
        Assert.All(Catalog.Models, m => Assert.True(
            m.FilamentDiameterMm is null or 1.75 or 2.85, $"{m.Id}: {m.FilamentDiameterMm}"));
    }

    /// <summary>Clay, paste and pellet machines take no filament.</summary>
    [Theory]
    [InlineData("eazao-m500")]
    [InlineData("eazao-m600")]
    [InlineData("eazao-m700")]
    [InlineData("eazao-potter")]
    [InlineData("eazao-zero")]
    [InlineData("fablabbcn-pasteprinter")]
    [InlineData("structur3d-io-discov3ry-complete")]
    public void ANonFilamentMachine_HasNoFilamentDiameter(string id)
    {
        Assert.Null(Assert.Single(Catalog.Models, m => m.Id == id).FilamentDiameterMm);
    }

    [Fact]
    public void TheDefaultNozzle_IsOneOfTheNozzleOptions()
    {
        Assert.All(
            Catalog.Models.Where(m => m.DefaultNozzleMm is not null && m.NozzleOptionsMm is { Count: > 0 }),
            m => Assert.True(m.NozzleOptionsMm!.Contains(m.DefaultNozzleMm!.Value), $"{m.Id}: {m.DefaultNozzleMm} not in [{string.Join(", ", m.NozzleOptionsMm!)}]"));
    }

    [Theory]
    [InlineData("prusa-mk4s-mmu3", "prusa-mk4s")]
    [InlineData("prusa-core-one-mmu3", "prusa-core-one")]
    public void AnMmu3Extra_TakesItsBaseModelsNozzles(string id, string baseId)
    {
        var extra = Assert.Single(Catalog.Models, m => m.Id == id);
        var baseModel = Assert.Single(Catalog.Models, m => m.Id == baseId);

        Assert.Equal(baseModel.NozzleOptionsMm, extra.NozzleOptionsMm);
        Assert.Equal([0.25, 0.3, 0.4, 0.5, 0.6, 0.8], extra.NozzleOptionsMm);
    }

    [Theory]
    [InlineData("orca-arena-x1-carbon")] // Orca's own demo copy of the X1 Carbon, not a product
    public void ADemoOrTemplatePrinter_IsHidden(string id)
    {
        Assert.DoesNotContain(Catalog.Models, m => m.Id == id);
        Assert.Contains(Catalog.Reserved, r => r.Id == id);
    }

    [Fact]
    public void EveryModel_HasABed_ExceptOnesAnOverrideExplains()
    {
        Assert.All(Catalog.Models, m => Assert.True(
            (m.BedWidthMm is not null && m.BedDepthMm is not null && m.BedHeightMm is not null)
                || OverrideExplains(m.Id, "note"),
            $"{m.Id}: {m.BedWidthMm} x {m.BedDepthMm} x {m.BedHeightMm}"));
    }

    [Theory]
    [InlineData("e3ng-v1-2s", "RH3D")] // https://rh3d.xyz (the E3NG's designer)
    [InlineData("lh-stinger", "lhndo")] // https://github.com/lhndo/LH-Stinger
    [InlineData("lh-stinger-mmu", "lhndo")]
    [InlineData("construct-1", "Construct3D")] // https://construct-3d.com
    [InlineData("construct-1-xl", "Construct3D")]
    public void AModelWhoseMakeOrcaGuessedWrong_HasItsRealBrand(string id, string make)
    {
        Assert.Equal(make, Assert.Single(Catalog.Models, m => m.Id == id).Make);
    }

    [Fact]
    public void TheLoader_SaysWhichResourceIsMissing()
    {
        var e = Assert.Throws<InvalidOperationException>(
            () => PrinterCatalogFileLoader.LoadEmbedded(typeof(PrinterCatalogGoldenTests).Assembly, "No.Such.catalog.json"));

        Assert.Contains("No.Such.catalog.json", e.Message, StringComparison.Ordinal);
    }

    private static void AssertSource(CatalogSource source, string repo, string license)
    {
        Assert.Equal(repo, source.Repo);
        Assert.Equal(license, source.License);
        Assert.Matches("^[0-9a-f]{40}$", source.Commit ?? "");
        Assert.False(string.IsNullOrWhiteSpace(source.Tag));
        Assert.NotEqual("placeholder", source.Tag);
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Slug();
}
