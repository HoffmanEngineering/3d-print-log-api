using System.Text.Json.Nodes;
using PrintLogApi.IntegrationTests.Analytics;
using PrintLogApi.PrinterCatalog.Generation;
using Xunit;

namespace PrintLogApi.IntegrationTests.PrinterCatalog;

/// <summary>
/// Ids across several generator runs (fix rounds 1 and 2). Ids follow each model's raw source
/// identities (<c>cura:&lt;definition&gt;</c>, <c>orca:&lt;vendor&gt;/&lt;model&gt;</c>), never its
/// aliased or overridden names. Each run reads the previous run's file back through JSON, as
/// the monthly refresh does.
/// </summary>
public class CatalogIdStabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly string FixtureRoot = Path.Combine(AppContext.BaseDirectory, "tests-fixtures", "printer-catalog");
    private static readonly string CuraFixture = Path.Combine(FixtureRoot, "cura");
    private static readonly string OrcaFixture = Path.Combine(FixtureRoot, "orca");
    private static readonly Dictionary<string, string> QuxAlias = new() { ["Qux3D"] = "Qux" };

    private static CatalogFile Run(
        string? curaDir,
        string? orcaDir,
        CatalogFile? previous,
        JsonObject? overrides = null,
        IReadOnlyDictionary<string, string>? aliases = null,
        List<string>? log = null)
    {
        var file = PrinterCatalogGenerator.Generate(new CatalogInputs
        {
            CuraDir = curaDir,
            OrcaDir = orcaDir,
            Sources = [new() { Repo = "SoftFever/OrcaSlicer", Tag = "v2.0.0", Commit = "bbbb", License = "AGPL-3.0" }],
            MakeAliases = aliases ?? QuxAlias,
            Overrides = overrides ?? [],
            Previous = previous,
            Clock = new SettableTimeProvider(Now),
            Log = log is null ? null : log.Add,
        });
        // Round-trip, so the next run sees exactly what was committed.
        return CatalogFile.Deserialize(CatalogFile.Serialize(file));
    }

    private static CatalogFile RunOrca(CatalogFile? previous, string[] modelNames, JsonObject? overrides = null, List<string>? log = null)
    {
        using var tree = new ProfileTree().OrcaVendor("Acme", modelNames);
        return Run(null, tree.Root, previous, overrides, log: log);
    }

    private static void AssertReserved(CatalogFile file, string id, params string[] sourceKeys) =>
        Assert.Equal(sourceKeys, Assert.Single(file.Reserved, r => r.Id == id).SourceKeys);

    // --- Round 1 cases, now on raw identities ---

    [Fact]
    public void Plus_IsItsOwnToken_SoMiniAndMiniPlusAreTwoModels()
    {
        using var tree = new ProfileTree().OrcaVendor("Prusa", ["Prusa MINI", "Prusa MINI+"]);

        var file = Run(null, tree.Root, previous: null);

        Assert.Equal(["prusa-mini", "prusa-mini-plus"], file.Models.Select(m => m.Id));
        Assert.Equal(["MINI", "MINI+"], file.Models.Select(m => m.Model));
        Assert.Equal(["orca:Prusa/Prusa MINI+"], file.Models[1].SourceKeys);
    }

    [Fact]
    public void ARetiredId_StaysReservedForever_AndADifferentModelWithItsSlugGetsASuffix()
    {
        var first = RunOrca(null, ["Acme Rocket-X", "Acme Other"]);
        Assert.Contains(first.Models, m => m.Id == "acme-rocket-x" && m.SourceKeys!.SequenceEqual(["orca:Acme/Acme Rocket-X"]));
        Assert.Empty(first.Reserved);

        // Run 2: it leaves the sources.
        var second = RunOrca(first, ["Acme Other"]);
        Assert.DoesNotContain(second.Models, m => m.Id == "acme-rocket-x");
        AssertReserved(second, "acme-rocket-x", "orca:Acme/Acme Rocket-X");

        // Run 3: "Rocket X" is another model that wants the same slug.
        var third = RunOrca(second, ["Acme Other", "Acme Rocket X"]);
        Assert.Equal("acme-rocket-x-2", Assert.Single(third.Models, m => m.Model == "Rocket X").Id);
        AssertReserved(third, "acme-rocket-x", "orca:Acme/Acme Rocket-X");

        // Run 4: the original comes back and reclaims its id, which leaves reserved.
        var fourth = RunOrca(third, ["Acme Other", "Acme Rocket X", "Acme Rocket-X"]);
        Assert.Equal("acme-rocket-x", Assert.Single(fourth.Models, m => m.Model == "Rocket-X").Id);
        Assert.Equal("acme-rocket-x-2", Assert.Single(fourth.Models, m => m.Model == "Rocket X").Id);
        Assert.DoesNotContain(fourth.Reserved, r => r.Id == "acme-rocket-x");
    }

    [Fact]
    public void ANameOverride_KeepsItsIdOnTheNextRun_WithoutADuplicate()
    {
        var overrides = JsonNode.Parse("""{ "foo-bar": { "make": "Foo Co", "model": "Bar Pro" } }""")!.AsObject();

        var first = Run(CuraFixture, OrcaFixture, null, overrides);
        var second = Run(CuraFixture, OrcaFixture, first, overrides);

        var fooBar = Assert.Single(second.Models, m => m.Id == "foo-bar");
        Assert.Equal("Foo Co", fooBar.Make);
        Assert.Equal("Bar Pro", fooBar.Model);
        Assert.Equal(["cura:foo_bar", "orca:Foo/Foo Bar"], fooBar.SourceKeys);
        Assert.DoesNotContain(second.Models, m => m.Id.StartsWith("foo-bar-", StringComparison.Ordinal));
        Assert.Equal(first.Models.Count, second.Models.Count);
        Assert.Equal(first.Version, second.Version);
    }

    [Fact]
    public void AHiddenModelsId_IsReserved_AndTheHideKeepsTargetingIt()
    {
        var overrides = JsonNode.Parse("""{ "zed-labs-z1-mini": { "hidden": true } }""")!.AsObject();

        var first = Run(CuraFixture, OrcaFixture, null, overrides);
        AssertReserved(first, "zed-labs-z1-mini", "orca:Zed/Zed Labs Z1 Mini");

        // A new vendor adds Zed | Labs Z1 Mini: another model, the same slug, and it sorts
        // before Zed Labs | Z1 Mini.
        using var orca = new ProfileTree().CopyFrom(OrcaFixture).OrcaVendor("Q", ["Zed Labs Z1 Mini", "Zed Other"]);
        var second = Run(CuraFixture, orca.Root, first, overrides);

        Assert.Equal("zed-labs-z1-mini-2", Assert.Single(second.Models, m => m.Make == "Zed" && m.Model == "Labs Z1 Mini").Id);
        Assert.DoesNotContain(second.Models, m => m.Make == "Zed Labs" && m.Model == "Z1 Mini");
        AssertReserved(second, "zed-labs-z1-mini", "orca:Zed/Zed Labs Z1 Mini");
    }

    [Fact]
    public void Reserved_IsSortedById_AndAnUnchangedRunIsByteIdentical()
    {
        var overrides = JsonNode.Parse("""{ "zed-labs-z1-mini": { "hidden": true }, "qux-one": { "hidden": true } }""")!.AsObject();

        var first = Run(CuraFixture, OrcaFixture, null, overrides);
        var second = Run(CuraFixture, OrcaFixture, first, overrides);

        Assert.Equal(["qux-one", "zed-labs-z1-mini"], first.Reserved.Select(r => r.Id));
        Assert.Equal(CatalogFile.Serialize(first), CatalogFile.Serialize(second));
    }

    // --- Round 2: aliases and sources never move an id ---

    [Fact]
    public void AddingAMakeAlias_KeepsTheId_AndShowsTheNewMake()
    {
        var first = Run(CuraFixture, null, null, aliases: new Dictionary<string, string>());
        Assert.Equal("Qux3D", Assert.Single(first.Models, m => m.Id == "qux3d-one").Make);

        var second = Run(CuraFixture, null, first, aliases: QuxAlias);

        var qux = Assert.Single(second.Models, m => m.SourceKeys!.Contains("cura:qux_one"));
        Assert.Equal("qux3d-one", qux.Id);
        Assert.Equal("Qux", qux.Make);
        Assert.Equal("One", qux.Model);
        Assert.Empty(second.Reserved);
    }

    [Fact]
    public void AnAliasThatChangesTheStrippedModelName_KeepsTheId()
    {
        using var cura = new ProfileTree()
            .CopyFrom(CuraFixture)
            .Write("resources/definitions/acme_rocket.def.json",
                """{ "name": "Acme Rocket", "version": 2, "inherits": "fdmprinter", "metadata": { "visible": true, "manufacturer": "Acme3D" } }""");

        // No alias: "Acme3D" is not a prefix of "Acme Rocket", so the name stays whole.
        var first = Run(cura.Root, null, null, aliases: new Dictionary<string, string>());
        var before = Assert.Single(first.Models, m => m.SourceKeys!.Contains("cura:acme_rocket"));
        Assert.Equal(("Acme3D", "Acme Rocket", "acme3d-acme-rocket"), (before.Make, before.Model, before.Id));

        // With Acme3D -> Acme the make prefix is stripped: Acme | Rocket.
        var second = Run(cura.Root, null, first, aliases: new Dictionary<string, string> { ["Acme3D"] = "Acme" });
        var after = Assert.Single(second.Models, m => m.SourceKeys!.Contains("cura:acme_rocket"));
        Assert.Equal(("Acme", "Rocket", "acme3d-acme-rocket"), (after.Make, after.Model, after.Id));
        Assert.Empty(second.Reserved);
    }

    [Fact]
    public void AModelInBothSources_KeepsItsId_WhenOnlyOneSourceStillHasIt()
    {
        var both = Run(CuraFixture, OrcaFixture, null);
        Assert.Equal(["cura:foo_bar", "orca:Foo/Foo Bar"], Assert.Single(both.Models, m => m.Id == "foo-bar").SourceKeys);

        var curaOnly = Run(CuraFixture, null, both);
        var orcaOnly = Run(null, OrcaFixture, both);

        // Keys accumulate (round 3): a source that drops out is still remembered.
        Assert.Equal(["cura:foo_bar", "orca:Foo/Foo Bar"], Assert.Single(curaOnly.Models, m => m.Id == "foo-bar").SourceKeys);
        Assert.Equal(["cura:foo_bar", "orca:Foo/Foo Bar"], Assert.Single(orcaOnly.Models, m => m.Id == "foo-bar").SourceKeys);
        Assert.DoesNotContain(curaOnly.Models, m => m.Id.StartsWith("foo-bar-", StringComparison.Ordinal));
        Assert.DoesNotContain(orcaOnly.Models, m => m.Id.StartsWith("foo-bar-", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExtraWhoseNamesMatchANewSourceModel_IsAdoptedByIt()
    {
        var overrides = JsonNode.Parse("""{ "acme-rocket-legacy": { "make": "Acme", "model": "Rocket", "bedWidthMm": 123 } }""")!.AsObject();
        var first = RunOrca(null, ["Acme Other"], overrides);
        Assert.Null(Assert.Single(first.Models, m => m.Id == "acme-rocket-legacy").SourceKeys);

        var second = RunOrca(first, ["Acme Other", "Acme Rocket"], overrides);

        var rocket = Assert.Single(second.Models, m => m.Model == "Rocket");
        Assert.Equal("acme-rocket-legacy", rocket.Id);
        Assert.Equal(["orca:Acme/Acme Rocket"], rocket.SourceKeys);
        // The extra entry now corrects the source model instead of standing beside it.
        Assert.Equal(123, rocket.BedWidthMm);
        Assert.Equal("override", rocket.Sources!["bedWidthMm"]);
        Assert.Equal("orca", rocket.Sources["bedDepthMm"]);
        Assert.DoesNotContain(second.Models, m => m.Id == "acme-rocket");
        Assert.Empty(second.Reserved);
    }

    [Fact]
    public void AnExtraWhoseNamesDiffer_IsKeptBesideTheSourceModel_AndLogged()
    {
        var overrides = JsonNode.Parse("""{ "acme-rocket-x": { "make": "Acme", "model": "Rocket X" } }""")!.AsObject();
        var first = RunOrca(null, ["Acme Other"], overrides);
        var log = new List<string>();

        // "Rocket-X" has the slug acme-rocket-x but a different name from the extra "Rocket X".
        var second = RunOrca(first, ["Acme Other", "Acme Rocket-X"], overrides, log);

        Assert.Null(Assert.Single(second.Models, m => m.Id == "acme-rocket-x").SourceKeys);
        Assert.Equal("acme-rocket-x-2", Assert.Single(second.Models, m => m.Model == "Rocket-X").Id);
        Assert.Contains(log, l => l.Contains("acme-rocket-x", StringComparison.Ordinal) && l.Contains("not adopted", StringComparison.Ordinal));
    }

    [Fact]
    public void HidingARetiredId_SaysSo()
    {
        var first = RunOrca(null, ["Acme Other", "Acme Gone"]);
        var log = new List<string>();

        var second = RunOrca(first, ["Acme Other"], JsonNode.Parse("""{ "acme-gone": { "hidden": true } }""")!.AsObject(), log);

        Assert.Contains(log, l => l.Contains("acme-gone", StringComparison.Ordinal) && l.Contains("hides a retired id", StringComparison.Ordinal));
        Assert.DoesNotContain(log, l => l.Contains("not a whole entry", StringComparison.Ordinal));
        AssertReserved(second, "acme-gone", "orca:Acme/Acme Gone");
    }

    [Fact]
    public void NoConfiguredSource_FailsTheRun()
    {
        var e = Assert.Throws<InvalidDataException>(() => PrinterCatalogGenerator.Generate(new CatalogInputs()));

        Assert.Contains("no source", e.Message, StringComparison.Ordinal);
    }

    // --- Round 3: merged ids and accumulated keys ---

    private const string SovolOrca = "orca:Sovol/Sovol SV06";
    private const string SovolCura = "cura:sovol_sv06";
    private const string Sovol1Cura = "cura:sovol1_sv06";

    private static string CuraDef(string name, string make) =>
        $$"""{ "name": "{{name}}", "version": 2, "inherits": "fdmprinter", "metadata": { "visible": true, "manufacturer": "{{make}}" } }""";

    private static ProfileTree SovolCuraTree(bool withSovol1 = false)
    {
        var tree = new ProfileTree().CopyFrom(CuraFixture)
            .Write("resources/definitions/sovol_sv06.def.json", CuraDef("Sovol SV06", "Sovol 3D"));
        return withSovol1 ? tree.Write("resources/definitions/sovol1_sv06.def.json", CuraDef("Sovol 1 SV06", "Sovol 1")) : tree;
    }

    [Fact]
    public void AnAliasThatMergesTwoPublishedIds_KeepsTheLowerOne_AndPointsTheOtherAtIt()
    {
        using var cura = SovolCuraTree();
        using var orca = new ProfileTree().OrcaVendor("Sovol", ["Sovol SV06"]);
        var noAlias = new Dictionary<string, string>();
        var sovolAlias = new Dictionary<string, string> { ["Sovol 3D"] = "Sovol" };

        // Run 1: Cura "Sovol 3D | Sovol SV06" and Orca "Sovol | SV06" are two models.
        var first = Run(cura.Root, orca.Root, null, aliases: noAlias);
        Assert.Equal([SovolCura], Assert.Single(first.Models, m => m.Id == "sovol-3d-sovol-sv06").SourceKeys);
        Assert.Equal([SovolOrca], Assert.Single(first.Models, m => m.Id == "sovol-sv06").SourceKeys);

        // Run 2: "Sovol 3D" -> "Sovol" joins them; the lower-sorting id survives.
        var second = Run(cura.Root, orca.Root, first, aliases: sovolAlias);
        var merged = Assert.Single(second.Models, m => m.SourceKeys!.Contains(SovolOrca));
        Assert.Equal("sovol-3d-sovol-sv06", merged.Id);
        Assert.Equal([SovolCura, SovolOrca], merged.SourceKeys);
        var loser = Assert.Single(second.Reserved);
        Assert.Equal(("sovol-sv06", "sovol-3d-sovol-sv06"), (loser.Id, loser.MergedInto));
        Assert.Equal([SovolOrca], loser.SourceKeys);

        // An unchanged rerun is byte-identical.
        var again = Run(cura.Root, orca.Root, second, aliases: sovolAlias);
        Assert.Equal(CatalogFile.Serialize(second), CatalogFile.Serialize(again));

        // Run 3: the alias goes away and the model splits; each half gets its own id back.
        var third = Run(cura.Root, orca.Root, again, aliases: noAlias);
        Assert.Equal([SovolCura], Assert.Single(third.Models, m => m.Id == "sovol-3d-sovol-sv06").SourceKeys);
        Assert.Equal([SovolOrca], Assert.Single(third.Models, m => m.Id == "sovol-sv06").SourceKeys);
        Assert.Empty(third.Reserved);

        // And the split is stable on the next run.
        var fourth = Run(cura.Root, orca.Root, third, aliases: noAlias);
        Assert.Equal(CatalogFile.Serialize(third), CatalogFile.Serialize(fourth));
    }

    [Fact]
    public void AMergeChain_KeepsEachEntryPointingAtItsDirectSuccessor()
    {
        using var cura = SovolCuraTree(withSovol1: true);
        using var orca = new ProfileTree().OrcaVendor("Sovol", ["Sovol SV06"]);

        var first = Run(cura.Root, orca.Root, null, aliases: new Dictionary<string, string>());
        Assert.Equal(
            ["sovol-1-sv06", "sovol-3d-sovol-sv06", "sovol-sv06"],
            first.Models.Where(m => m.Id.StartsWith("sovol", StringComparison.Ordinal)).Select(m => m.Id).Order(StringComparer.Ordinal));

        // Run 2: sovol-sv06 merges into sovol-3d-sovol-sv06.
        var second = Run(cura.Root, orca.Root, first, aliases: new Dictionary<string, string> { ["Sovol 3D"] = "Sovol" });
        Assert.Equal("sovol-3d-sovol-sv06", Assert.Single(second.Reserved, r => r.Id == "sovol-sv06").MergedInto);

        // Run 3: "Sovol 1" joins too, and the lower-sorting sovol-1-sv06 absorbs the survivor.
        var third = Run(cura.Root, orca.Root, second,
            aliases: new Dictionary<string, string> { ["Sovol 3D"] = "Sovol", ["Sovol 1"] = "Sovol" });
        var merged = Assert.Single(third.Models, m => m.SourceKeys!.Contains(SovolOrca));
        Assert.Equal("sovol-1-sv06", merged.Id);
        Assert.Equal([Sovol1Cura, SovolCura, SovolOrca], merged.SourceKeys);
        Assert.Equal("sovol-1-sv06", Assert.Single(third.Reserved, r => r.Id == "sovol-3d-sovol-sv06").MergedInto);
        Assert.Equal("sovol-3d-sovol-sv06", Assert.Single(third.Reserved, r => r.Id == "sovol-sv06").MergedInto);
    }

    // --- Round 4: a merged id stays put unless the model truly splits ---

    private static readonly Dictionary<string, string> SovolAlias = new() { ["Sovol 3D"] = "Sovol" };

    /// <summary>Runs 1 and 2 of the merge test: sovol-sv06 is merged into sovol-3d-sovol-sv06.</summary>
    private static CatalogFile MergedSovol(string curaRoot, string orcaRoot)
    {
        var first = Run(curaRoot, orcaRoot, null, aliases: new Dictionary<string, string>());
        var second = Run(curaRoot, orcaRoot, first, aliases: SovolAlias);
        Assert.Equal("sovol-3d-sovol-sv06", Assert.Single(second.Models, m => m.SourceKeys!.Contains(SovolOrca)).Id);
        Assert.Equal("sovol-3d-sovol-sv06", Assert.Single(second.Reserved, r => r.Id == "sovol-sv06").MergedInto);
        return second;
    }

    [Theory]
    [InlineData("cura")]
    [InlineData("orca")]
    public void DroppingOneSourceOfAMergedModel_NeverChangesItsId(string dropped)
    {
        using var cura = SovolCuraTree();
        using var orca = new ProfileTree().OrcaVendor("Sovol", ["Sovol SV06"]);
        var merged = MergedSovol(cura.Root, orca.Root);
        var remaining = dropped == "cura" ? SovolOrca : SovolCura;

        // The dropped side's Sovol printer is gone; the Cura fixture keeps the run non-empty.
        CatalogFile Next(CatalogFile previous) => dropped == "cura"
            ? Run(CuraFixture, orca.Root, previous, aliases: SovolAlias)
            : Run(cura.Root, null, previous, aliases: SovolAlias);

        var third = Next(merged);
        var fourth = Next(third);
        var fifth = Next(fourth);

        foreach (var file in new[] { third, fourth, fifth })
        {
            var model = Assert.Single(file.Models, m => m.SourceKeys!.Contains(remaining));
            Assert.Equal("sovol-3d-sovol-sv06", model.Id);
            Assert.Equal([SovolCura, SovolOrca], model.SourceKeys);
            Assert.DoesNotContain(file.Models, m => m.Id == "sovol-sv06");
            Assert.Equal(
                System.Text.Json.JsonSerializer.Serialize(merged.Reserved, CatalogFile.JsonOptions),
                System.Text.Json.JsonSerializer.Serialize(file.Reserved, CatalogFile.JsonOptions));
        }
        Assert.Equal(CatalogFile.Serialize(third), CatalogFile.Serialize(fourth));
        Assert.Equal(CatalogFile.Serialize(fourth), CatalogFile.Serialize(fifth));
    }

    [Fact]
    public void HidingAMergedIdThenDroppingTheSurvivorsOtherSource_LeavesNoMergedIntoCycle()
    {
        using var cura = SovolCuraTree();
        using var orca = new ProfileTree().OrcaVendor("Sovol", ["Sovol SV06"]);
        var hide = JsonNode.Parse("""{ "sovol-sv06": { "hidden": true } }""")!.AsObject();
        var merged = MergedSovol(cura.Root, orca.Root);
        var log = new List<string>();

        // The hide targets the merged-away id; the Cura side then leaves.
        var third = Run(CuraFixture, orca.Root, merged, hide, SovolAlias, log);
        var fourth = Run(CuraFixture, orca.Root, third, hide, SovolAlias);

        foreach (var file in new[] { third, fourth })
        {
            Assert.Equal("sovol-3d-sovol-sv06", Assert.Single(file.Models, m => m.SourceKeys!.Contains(SovolOrca)).Id);
            Assert.Equal("sovol-3d-sovol-sv06", Assert.Single(file.Reserved, r => r.Id == "sovol-sv06").MergedInto);
            AssertNoMergedIntoCycle(file);
        }
        Assert.Contains(log, l => l.Contains("sovol-sv06", StringComparison.Ordinal) && l.Contains("hides a retired id", StringComparison.Ordinal));
        Assert.Equal(CatalogFile.Serialize(third), CatalogFile.Serialize(fourth));
    }

    [Fact]
    public void AReclaimedButHiddenId_DropsItsOldMergedInto()
    {
        using var cura = SovolCuraTree();
        using var orca = new ProfileTree().OrcaVendor("Sovol", ["Sovol SV06"]);
        var hide = JsonNode.Parse("""{ "sovol-sv06": { "hidden": true } }""")!.AsObject();
        var merged = MergedSovol(cura.Root, orca.Root);

        // The alias goes away (a true split): the Orca half reclaims sovol-sv06, which is hidden.
        var split = Run(cura.Root, orca.Root, merged, hide, new Dictionary<string, string>());

        Assert.Equal([SovolCura], Assert.Single(split.Models, m => m.Id == "sovol-3d-sovol-sv06").SourceKeys);
        var hidden = Assert.Single(split.Reserved, r => r.Id == "sovol-sv06");
        Assert.Equal([SovolOrca], hidden.SourceKeys);
        Assert.Null(hidden.MergedInto);
        AssertNoMergedIntoCycle(split);
    }

    [Fact]
    public void AMergedIntoCycle_FailsTheRun()
    {
        var previous = new CatalogFile
        {
            Reserved =
            [
                new ReservedId { Id = "loop-a", SourceKeys = ["orca:Gone/A"], MergedInto = "loop-b" },
                new ReservedId { Id = "loop-b", SourceKeys = ["orca:Gone/B"], MergedInto = "loop-a" },
            ],
        };
        using var tree = new ProfileTree().OrcaVendor("Acme", ["Acme Other"]);

        var e = Assert.Throws<InvalidDataException>(() => Run(null, tree.Root, previous));

        Assert.Contains("cycle", e.Message, StringComparison.Ordinal);
        Assert.Contains("loop-a", e.Message, StringComparison.Ordinal);
    }

    private static void AssertNoMergedIntoCycle(CatalogFile file)
    {
        var next = file.Reserved.Where(r => r.MergedInto is not null).ToDictionary(r => r.Id, r => r.MergedInto!);
        foreach (var start in next.Keys)
        {
            var seen = new HashSet<string>();
            for (var id = start; id is not null; id = next.GetValueOrDefault(id))
            {
                Assert.True(seen.Add(id), $"mergedInto cycle through {id}");
            }
        }
    }

    [Fact]
    public void SourceKeysAccumulate_SoAModelThatSwitchesSourcesKeepsItsId()
    {
        var both = Run(CuraFixture, OrcaFixture, null);
        var curaOnly = Run(CuraFixture, null, both);
        var orcaOnly = Run(null, OrcaFixture, curaOnly);

        var fooBar = Assert.Single(orcaOnly.Models, m => m.Make == "Foo" && m.Model == "Bar");
        Assert.Equal("foo-bar", fooBar.Id);
        Assert.Equal(["cura:foo_bar", "orca:Foo/Foo Bar"], fooBar.SourceKeys);
    }

    [Fact]
    public void ANewExtraCollidingWithANewSourceModel_IsLogged()
    {
        var overrides = JsonNode.Parse("""{ "acme-rocket": { "make": "Acme", "model": "Rocket Mk2" } }""")!.AsObject();
        var log = new List<string>();

        var file = RunOrca(null, ["Acme Rocket", "Acme Other"], overrides, log);

        // Accepted edge case: the entry corrects the source model rather than standing beside it.
        Assert.Equal("Rocket Mk2", Assert.Single(file.Models, m => m.Id == "acme-rocket").Model);
        Assert.Contains(log, l => l.Contains("acme-rocket", StringComparison.Ordinal) && l.Contains("collides", StringComparison.Ordinal));
    }
}
