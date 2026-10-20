using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PrintLogApi.IntegrationTests.Analytics;
using PrintLogApi.PrinterCatalog.Generation;
using Xunit;

namespace PrintLogApi.IntegrationTests.PrinterCatalog;

/// <summary>
/// The printer catalog generator against the minimal Cura tree and Orca profile folder in
/// tests-fixtures/printer-catalog. The fixtures carry G-code, platform meshes and bed textures
/// on purpose, so the output tests can prove none of it is copied.
/// </summary>
public class CatalogGeneratorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static readonly string FixtureRoot = Path.Combine(AppContext.BaseDirectory, "tests-fixtures", "printer-catalog");
    private static readonly string CuraDir = Path.Combine(FixtureRoot, "cura");
    private static readonly string OrcaDir = Path.Combine(FixtureRoot, "orca");

    private static readonly Dictionary<string, string> Aliases = new() { ["Qux3D"] = "Qux" };
    private static readonly CatalogNormalizer Normalizer = new(Aliases);

    private static readonly CatalogSource[] Sources =
    [
        new() { Repo = "Ultimaker/Cura", Tag = "5.0.0", Commit = "aaaa", License = "LGPL-3.0" },
        new() { Repo = "SoftFever/OrcaSlicer", Tag = "v2.0.0", Commit = "bbbb", License = "AGPL-3.0" },
    ];

    private static CatalogInputs Inputs(JsonObject? overrides = null, CatalogFile? previous = null) => new()
    {
        CuraDir = CuraDir,
        OrcaDir = OrcaDir,
        Sources = Sources,
        MakeAliases = Aliases,
        Overrides = overrides ?? [],
        Previous = previous,
        Clock = new SettableTimeProvider(Now),
    };

    private static CatalogModel Single(IEnumerable<CatalogModel> models, string make, string model) =>
        Assert.Single(models, m => m.Make == make && m.Model == model);

    // --- Cura ---

    [Fact]
    public void Cura_PrinterInheritingABase_GetsTheParentsBedAndItsOwnNozzleFromTheDefaultVariant()
    {
        var models = CuraProfileReader.Read(CuraDir, Normalizer);

        var fooBar = Single(models, "Foo", "Bar");
        Assert.Equal(220, fooBar.BedWidthMm);
        Assert.Equal(220, fooBar.BedDepthMm);
        // foo_base.preferred_variant_name is "0.6mm Nozzle". It beats the extruder's 0.5 and
        // fdmprinter's 0.4.
        Assert.Equal(0.6, fooBar.DefaultNozzleMm);
        Assert.Equal([0.4, 0.6], fooBar.NozzleOptionsMm);
        // The extruder definition's material_diameter beats fdmprinter's 2.85.
        Assert.Equal(1.75, fooBar.FilamentDiameterMm);
        // A literal "value" in the base's override wins over fdmprinter's default_value.
        Assert.True(fooBar.HasHeatedBed);
        Assert.False(fooBar.HasHeatedChamber);
        Assert.Equal(1, fooBar.ExtruderCount);
        Assert.Equal("FFF", fooBar.Technology);
        Assert.Equal("cura", fooBar.Sources!["bedWidthMm"]);
    }

    [Fact]
    public void Cura_AValueThatIsNotALiteral_IsIgnored()
    {
        var models = CuraProfileReader.Read(CuraDir, Normalizer);

        // foo_bar sets machine_height { "value": "machine_width * 2" }. That is an expression,
        // so the inherited default_value (250) stands; evaluating it would give 440.
        Assert.Equal(250, Single(models, "Foo", "Bar").BedHeightMm);
    }

    [Fact]
    public void Cura_SkipsBaseExtruderAndCustomDefinitions_AndAliasesTheMake()
    {
        var models = CuraProfileReader.Read(CuraDir, Normalizer);

        Assert.Equal(["Foo|Bar", "Qux|One"], models.Select(m => $"{m.Make}|{m.Model}").Order(StringComparer.Ordinal));

        var qux = Single(models, "Qux", "One");
        Assert.Equal(300, qux.BedWidthMm);
        Assert.Equal(310, qux.BedDepthMm);
        Assert.Equal(320, qux.BedHeightMm);
        Assert.True(qux.HasHeatedChamber);
        Assert.Equal(2, qux.ExtruderCount);
        Assert.Equal(0.4, qux.DefaultNozzleMm);
        Assert.Equal(2.85, qux.FilamentDiameterMm);
    }

    // --- Orca ---

    [Fact]
    public void Orca_BedIsTheBoundingBox_The04PresetIsChosen_AndNozzleOptionsComeFromTheModel()
    {
        var models = OrcaProfileReader.Read(OrcaDir, Normalizer);

        var fooBar = Single(models, "Foo", "Bar");
        // printable_area of the 0.4 preset runs from -5 to 225 by -5 to 220.
        Assert.Equal(230, fooBar.BedWidthMm);
        Assert.Equal(225, fooBar.BedDepthMm);
        // printable_height comes from fdm_foo_common, overriding fdm_machine_common.
        Assert.Equal(235, fooBar.BedHeightMm);
        Assert.Equal(0.4, fooBar.DefaultNozzleMm);
        Assert.Equal([0.4, 0.6], fooBar.NozzleOptionsMm);
        Assert.Equal(1.75, fooBar.FilamentDiameterMm);
        Assert.Equal(1, fooBar.ExtruderCount);
        Assert.Equal("corexy", fooBar.Kinematics);
        Assert.Equal("orca", fooBar.Sources!["bedWidthMm"]);
    }

    [Fact]
    public void Orca_MakeIsTheVendorsCommonModelPrefix_AndCustomAndBlacklistAreSkipped()
    {
        var models = OrcaProfileReader.Read(OrcaDir, Normalizer);

        Assert.Equal(
            ["Foo|Bar", "Zed Labs|Z1", "Zed Labs|Z1 Mini"],
            models.Select(m => $"{m.Make}|{m.Model}").Order(StringComparer.Ordinal));

        var z1 = Single(models, "Zed Labs", "Z1");
        Assert.Equal(2, z1.ExtruderCount);
        Assert.Equal("i3", z1.Kinematics);

        // No 0.4 preset: the first one is used.
        var mini = Single(models, "Zed Labs", "Z1 Mini");
        Assert.Equal(0.2, mini.DefaultNozzleMm);
        Assert.Equal(180, mini.BedWidthMm);
        // fdm_machine_common says nothing about printer_structure.
        Assert.Null(mini.Kinematics);
    }

    // --- Merge, overrides, ids ---

    [Fact]
    public void Merge_AModelInBothSources_TakesTheOrcaBed_AndRecordsWhereEachFieldCameFrom()
    {
        var file = PrinterCatalogGenerator.Generate(Inputs());

        var fooBar = Single(file.Models, "Foo", "Bar");
        Assert.Equal("foo-bar", fooBar.Id);
        Assert.Equal(230, fooBar.BedWidthMm);
        Assert.Equal(225, fooBar.BedDepthMm);
        Assert.Equal(235, fooBar.BedHeightMm);
        Assert.Equal(0.4, fooBar.DefaultNozzleMm);
        Assert.Equal("orca", fooBar.Sources!["bedWidthMm"]);
        Assert.Equal("orca", fooBar.Sources["defaultNozzleMm"]);
        // Only Cura knows about the heated bed.
        Assert.True(fooBar.HasHeatedBed);
        Assert.Equal("cura", fooBar.Sources["hasHeatedBed"]);
    }

    [Fact]
    public void Overrides_AFieldWins_HiddenDropsAModel_AndAnExtraEntryIsAdded()
    {
        var overrides = JsonNode.Parse("""
            {
              "qux-one": { "bedWidthMm": 305, "slotCount": 4, "aliases": ["Q1"], "featured": 1 },
              "zed-labs-z1-mini": { "hidden": true },
              "resin-co-r1": { "make": "Resin Co", "model": "R1", "technology": "Resin", "bedWidthMm": 120, "bedDepthMm": 68, "bedHeightMm": 150 }
            }
            """)!.AsObject();

        var file = PrinterCatalogGenerator.Generate(Inputs(overrides));

        var qux = Assert.Single(file.Models, m => m.Id == "qux-one");
        Assert.Equal(305, qux.BedWidthMm);
        Assert.Equal("override", qux.Sources!["bedWidthMm"]);
        Assert.Equal(4, qux.DefaultSlotCount);
        Assert.Equal("override", qux.Sources["defaultSlotCount"]);
        Assert.Equal(["Q1"], qux.Aliases);
        Assert.Equal(1, qux.Featured);
        // Untouched fields keep their source.
        Assert.Equal("cura", qux.Sources["bedDepthMm"]);

        Assert.DoesNotContain(file.Models, m => m.Id == "zed-labs-z1-mini");

        var resin = Assert.Single(file.Models, m => m.Id == "resin-co-r1");
        Assert.Equal("Resin Co", resin.Make);
        Assert.Equal("R1", resin.Model);
        Assert.Equal("Resin", resin.Technology);
        Assert.Equal(120, resin.BedWidthMm);
        Assert.Equal("override", resin.Sources!["bedWidthMm"]);
    }

    [Fact]
    public void AMultiExtruderModel_GetsASlotPerExtruder_FromTheSameSource()
    {
        var file = PrinterCatalogGenerator.Generate(Inputs());

        var z1 = Assert.Single(file.Models, m => m.Id == "zed-labs-z1");
        Assert.Equal(2, z1.ExtruderCount);
        Assert.Equal(2, z1.DefaultSlotCount);
        Assert.Equal(z1.Sources!["extruderCount"], z1.Sources["defaultSlotCount"]);
        // A single extruder says nothing about slots.
        Assert.Null(Assert.Single(file.Models, m => m.Id == "foo-bar").DefaultSlotCount);
    }

    [Fact]
    public void Overrides_ANullClearsASourceValue_AndItsSource()
    {
        var overrides = JsonNode.Parse("""
            { "foo-bar": { "filamentDiameterMm": null, "note": "A paste extruder." } }
            """)!.AsObject();

        var fooBar = Assert.Single(PrinterCatalogGenerator.Generate(Inputs(overrides)).Models, m => m.Id == "foo-bar");

        Assert.Null(fooBar.FilamentDiameterMm);
        Assert.False(fooBar.Sources!.ContainsKey("filamentDiameterMm"));
    }

    [Fact]
    public void Overrides_ANoteIsForReviewersOnly_AndNeverReachesTheFile()
    {
        var overrides = JsonNode.Parse("""
            { "qux-one": { "bedWidthMm": 305, "note": "The manufacturer lists 305 mm." } }
            """)!.AsObject();

        var file = PrinterCatalogGenerator.Generate(Inputs(overrides));

        Assert.Equal(305, Assert.Single(file.Models, m => m.Id == "qux-one").BedWidthMm);
        Assert.DoesNotContain("manufacturer lists", CatalogFile.Serialize(file), StringComparison.Ordinal);
    }

    [Fact]
    public void Ids_ARenameThatKeepsTheKeyKeepsTheId_AndANewCollisionGetsASuffix()
    {
        var previous = new CatalogFile
        {
            Models =
            [
                new() { Id = "foo-bar", Make = "Foo", Model = "Bar", Technology = "FFF" },
                // Retired: no longer generated, but its id stays reserved.
                new() { Id = "gone-one", Make = "Old", Model = "Name", Technology = "FFF" },
            ],
        };

        var assigned = CatalogIds.Assign(
            [
                // Same key as Foo|Bar ("foo|bar"), though its own slug would be "foo-b-ar".
                new() { Make = "Foo", Model = "B-ar", Technology = "FFF" },
                // Different keys ("foo|bar baz" and "foo bar|baz"), same slug "foo-bar-baz".
                new() { Make = "Foo Bar", Model = "Baz", Technology = "FFF" },
                new() { Make = "Foo", Model = "Bar Baz", Technology = "FFF" },
                // Its slug "gone-one" belongs to the retired Old|Name.
                new() { Make = "Gone", Model = "One", Technology = "FFF" },
            ],
            previous);

        Assert.Equal("foo-bar", Assert.Single(assigned, m => m.Model == "B-ar").Id);
        Assert.Equal("foo-bar-baz", Assert.Single(assigned, m => m.Make == "Foo" && m.Model == "Bar Baz").Id);
        Assert.Equal("foo-bar-baz-2", Assert.Single(assigned, m => m.Make == "Foo Bar").Id);
        Assert.Equal("gone-one-2", Assert.Single(assigned, m => m.Make == "Gone").Id);
    }

    [Fact]
    public void Ids_TheGeneratorKeepsIdsFromThePreviousFile()
    {
        var previous = new CatalogFile
        {
            Models = [new() { Id = "foo-bar-original", Make = "FOO", Model = "Bar!", Technology = "FFF" }],
        };

        var file = PrinterCatalogGenerator.Generate(Inputs(previous: previous));

        Assert.Equal("foo-bar-original", Single(file.Models, "Foo", "Bar").Id);
    }

    // --- Output ---

    [Fact]
    public void Output_IsSorted_VersionedByTheModelsHash_AndOmitsUnknownFields()
    {
        var file = PrinterCatalogGenerator.Generate(Inputs());

        Assert.Equal(
            ["Foo|Bar", "Qux|One", "Zed Labs|Z1", "Zed Labs|Z1 Mini"],
            file.Models.Select(m => $"{m.Make}|{m.Model}"));

        var expectedVersion = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(file.Models, CatalogFile.JsonOptions))));
        Assert.Equal(expectedVersion, file.Version);
        Assert.Equal(Now, file.GeneratedAt);
        Assert.Equal(Sources, file.Sources);
        Assert.Equal(
            "Printer specifications derived from Ultimaker Cura (LGPL-3.0) and OrcaSlicer (AGPL-3.0) printer profiles.",
            file.Attribution);

        var json = JsonNode.Parse(CatalogFile.Serialize(file))!.AsObject();
        var qux = json["models"]!.AsArray().Single(m => (string?)m!["id"] == "qux-one")!.AsObject();
        // Cura knows nothing about kinematics or aliases: the keys are absent, not null.
        Assert.False(qux.ContainsKey("kinematics"));
        Assert.False(qux.ContainsKey("aliases"));
        // A single-extruder model has no slot count either.
        var fooBar = json["models"]!.AsArray().Single(m => (string?)m!["id"] == "foo-bar")!.AsObject();
        Assert.False(fooBar.ContainsKey("defaultSlotCount"));
        Assert.False(qux.ContainsKey("featured"));
        Assert.Equal(300, (double)qux["bedWidthMm"]!);
    }

    [Fact]
    public void Output_CarriesFactsOnly_NoGcodeMeshesOrTextures()
    {
        var text = CatalogFile.Serialize(PrinterCatalogGenerator.Generate(Inputs()));

        Assert.DoesNotContain("machine_start_gcode", text);
        Assert.DoesNotContain("bed_model", text);
        Assert.DoesNotContain("platform", text);
        foreach (var banned in new[] { "gcode", "stl", "3mf", "svg" })
        {
            Assert.DoesNotContain(banned, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Output_RegeneratingAnUnchangedCatalog_KeepsTheVersionAndTimestamp()
    {
        var first = PrinterCatalogGenerator.Generate(Inputs());
        var reread = CatalogFile.Deserialize(CatalogFile.Serialize(first));

        var second = PrinterCatalogGenerator.Generate(Inputs(previous: reread) with { Clock = new SettableTimeProvider(Now.AddDays(30)) });

        Assert.Equal(first.Version, second.Version);
        Assert.Equal(first.GeneratedAt, second.GeneratedAt);
        Assert.Equal(CatalogFile.Serialize(first), CatalogFile.Serialize(second));
    }

    // --- Summary ---

    private static CatalogModel Model(string id, string make, string model, double? bedWidthMm = 220) =>
        new() { Id = id, Make = make, Model = model, BedWidthMm = bedWidthMm };

    [Fact]
    public void Summary_AOneModelAddition_IsReportedAsAdded()
    {
        var previous = new CatalogFile { Models = [Model("foo-bar", "Foo", "Bar")] };
        var current = new CatalogFile { Models = [Model("foo-bar", "Foo", "Bar"), Model("make-model", "Make", "Model")] };

        var summary = CatalogSummary.Build(previous, current, new HashSet<string>());

        Assert.Contains("Added: Make Model", summary);
        Assert.DoesNotContain("Changed:", summary);
        Assert.DoesNotContain("Retired:", summary);
    }

    [Fact]
    public void Summary_AFieldChange_ListsTheField()
    {
        var previous = new CatalogFile { Models = [Model("foo-bar", "Foo", "Bar", 220)] };
        var current = new CatalogFile { Models = [Model("foo-bar", "Foo", "Bar", 250)] };

        var summary = CatalogSummary.Build(previous, current, new HashSet<string>());

        Assert.Contains("Changed: Foo Bar", summary);
        Assert.Contains("bedWidthMm", summary);
        Assert.DoesNotContain("Added:", summary);
    }

    [Fact]
    public void Summary_RetiredHiddenAndMergedModels_AreTold_ApartFromEachOther()
    {
        var previous = new CatalogFile
        {
            Models = [Model("gone-one", "Gone", "One"), Model("hid-den", "Hid", "Den"), Model("old-id", "Old", "Id"), Model("new-id", "New", "Id")],
        };
        var current = new CatalogFile
        {
            Models = [Model("new-id", "New", "Id")],
            Reserved =
            [
                new() { Id = "gone-one" },
                new() { Id = "hid-den" },
                new() { Id = "old-id", MergedInto = "new-id" },
            ],
        };

        var summary = CatalogSummary.Build(previous, current, new HashSet<string> { "hid-den" });

        Assert.Contains("Retired: Gone One", summary);
        Assert.Contains("Hidden: Hid Den", summary);
        Assert.Contains("Merged: Old Id into new-id", summary);
        Assert.DoesNotContain("Retired: Hid Den", summary);
        Assert.DoesNotContain("Retired: Old Id", summary);
    }

    [Fact]
    public void Summary_NothingChanged_SaysSo()
    {
        var file = new CatalogFile { Models = [Model("foo-bar", "Foo", "Bar")] };

        var summary = CatalogSummary.Build(file, file, new HashSet<string>());

        Assert.Contains("No changes", summary);
    }

    [Fact]
    public void Summary_ASectionIsCapped_AndSaysHowManyMore()
    {
        var current = new CatalogFile { Models = Enumerable.Range(0, 130).Select(i => Model($"m-{i:000}", "M", $"{i:000}")).ToList() };

        var summary = CatalogSummary.Build(new CatalogFile(), current, new HashSet<string>());

        Assert.Equal(100, summary.Split('\n').Count(l => l.StartsWith("- Added:", StringComparison.Ordinal)));
        Assert.Contains("and 30 more", summary);
    }

    // --- Normalizer ---

    [Fact]
    public void Normalizer_Key_LowercasesStripsPunctuationCollapsesWhitespace_AndAppliesAliases()
    {
        Assert.Equal("qux|one mini", Normalizer.Key("qux3d", "  One   Mini! "));
        Assert.Equal("foo|ender3 v2", Normalizer.Key("Foo", "Ender-3 V2"));
        Assert.Equal(["ender3", "v2", "neo"], CatalogNormalizer.Tokens("Ender-3  V2 Neo"));
    }
}
