using System.Text.Json.Nodes;
using PrintLogApi.IntegrationTests.Analytics;
using PrintLogApi.PrinterCatalog.Generation;
using Xunit;

namespace PrintLogApi.IntegrationTests.PrinterCatalog;

/// <summary>
/// Ids across several generator runs (fix round 1, rulings R1 and R2). Each run reads the
/// previous run's file back through JSON, as the monthly refresh does.
/// </summary>
public class CatalogIdStabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly string FixtureRoot = Path.Combine(AppContext.BaseDirectory, "tests-fixtures", "printer-catalog");
    private static readonly string CuraFixture = Path.Combine(FixtureRoot, "cura");
    private static readonly string OrcaFixture = Path.Combine(FixtureRoot, "orca");

    private static CatalogFile Run(string? curaDir, string? orcaDir, CatalogFile? previous, JsonObject? overrides = null)
    {
        var file = PrinterCatalogGenerator.Generate(new CatalogInputs
        {
            CuraDir = curaDir,
            OrcaDir = orcaDir,
            Sources = [new() { Repo = "SoftFever/OrcaSlicer", Tag = "v2.0.0", Commit = "bbbb", License = "AGPL-3.0" }],
            MakeAliases = new Dictionary<string, string> { ["Qux3D"] = "Qux" },
            Overrides = overrides ?? [],
            Previous = previous,
            Clock = new SettableTimeProvider(Now),
        });
        // Round-trip, so the next run sees exactly what was committed.
        return CatalogFile.Deserialize(CatalogFile.Serialize(file));
    }

    private static CatalogFile RunOrca(CatalogFile? previous, params string[] modelNames)
    {
        using var tree = new ProfileTree().OrcaVendor("Acme", modelNames);
        return Run(null, tree.Root, previous);
    }

    [Fact]
    public void Plus_IsItsOwnToken_SoMiniAndMiniPlusAreTwoModels()
    {
        using var tree = new ProfileTree().OrcaVendor("Prusa", ["Prusa MINI", "Prusa MINI+"]);

        var file = Run(null, tree.Root, previous: null);

        Assert.Equal(["prusa-mini", "prusa-mini-plus"], file.Models.Select(m => m.Id));
        Assert.Equal(["MINI", "MINI+"], file.Models.Select(m => m.Model));
        Assert.Equal("prusa|mini plus", file.Models[1].SourceKey);
    }

    [Fact]
    public void ARetiredId_StaysReservedForever_AndADifferentModelWithItsSlugGetsASuffix()
    {
        // Run 1: "Rocket-X" has the key acme|rocketx and the slug acme-rocket-x.
        var first = RunOrca(null, "Acme Rocket-X", "Acme Other");
        Assert.Contains(first.Models, m => m.Id == "acme-rocket-x" && m.SourceKey == "acme|rocketx");
        Assert.Empty(first.Reserved);

        // Run 2: it leaves the sources.
        var second = RunOrca(first, "Acme Other");
        Assert.DoesNotContain(second.Models, m => m.Id == "acme-rocket-x");
        Assert.Contains(new ReservedId { Id = "acme-rocket-x", SourceKey = "acme|rocketx" }, second.Reserved);

        // Run 3: "Rocket X" (key acme|rocket x) wants the same slug, two runs after the id
        // was last published.
        var third = RunOrca(second, "Acme Other", "Acme Rocket X");
        Assert.Equal("acme-rocket-x-2", Assert.Single(third.Models, m => m.Model == "Rocket X").Id);
        Assert.Contains(new ReservedId { Id = "acme-rocket-x", SourceKey = "acme|rocketx" }, third.Reserved);

        // Run 4: the original comes back and reclaims its id, which leaves reserved.
        var fourth = RunOrca(third, "Acme Other", "Acme Rocket X", "Acme Rocket-X");
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
        Assert.Equal("foo|bar", fooBar.SourceKey);
        Assert.DoesNotContain(second.Models, m => m.Id.StartsWith("foo-bar-", StringComparison.Ordinal));
        Assert.Equal(first.Models.Count, second.Models.Count);
        Assert.Equal(first.Version, second.Version);
    }

    [Fact]
    public void AHiddenModelsId_IsReserved_AndTheHideKeepsTargetingIt()
    {
        var overrides = JsonNode.Parse("""{ "zed-labs-z1-mini": { "hidden": true } }""")!.AsObject();

        var first = Run(CuraFixture, OrcaFixture, null, overrides);
        Assert.Contains(new ReservedId { Id = "zed-labs-z1-mini", SourceKey = "zed labs|z1 mini" }, first.Reserved);

        // A new vendor adds Zed | Labs Z1 Mini: another key, the same slug, and it sorts
        // before Zed Labs | Z1 Mini.
        using var orca = new ProfileTree().CopyFrom(OrcaFixture).OrcaVendor("Q", ["Zed Labs Z1 Mini", "Zed Other"]);
        var second = Run(CuraFixture, orca.Root, first, overrides);

        Assert.Equal("zed-labs-z1-mini-2", Assert.Single(second.Models, m => m.Make == "Zed" && m.Model == "Labs Z1 Mini").Id);
        Assert.DoesNotContain(second.Models, m => m.Make == "Zed Labs" && m.Model == "Z1 Mini");
        Assert.Contains(new ReservedId { Id = "zed-labs-z1-mini", SourceKey = "zed labs|z1 mini" }, second.Reserved);
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
}
