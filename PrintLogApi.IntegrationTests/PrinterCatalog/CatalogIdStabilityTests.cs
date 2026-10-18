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

        Assert.Equal(["cura:foo_bar"], Assert.Single(curaOnly.Models, m => m.Id == "foo-bar").SourceKeys);
        Assert.Equal(["orca:Foo/Foo Bar"], Assert.Single(orcaOnly.Models, m => m.Id == "foo-bar").SourceKeys);
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
}
