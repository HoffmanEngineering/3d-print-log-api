using PrintLogApi.PrinterCatalog.Generation;
using Xunit;

namespace PrintLogApi.IntegrationTests.PrinterCatalog;

/// <summary>
/// Broken or missing profile input (fix round 1). A bad file is skipped and logged; a source
/// that is missing or yields nothing fails the run (ruling R3).
/// </summary>
public class CatalogProfileRobustnessTests
{
    private static readonly string CuraFixture = Path.Combine(AppContext.BaseDirectory, "tests-fixtures", "printer-catalog", "cura");

    private const string FdmBase = "resources/definitions/";

    private static string CuraPrinter(string name, string inherits, string make = "Acme") =>
        $$"""{ "name": "{{name}}", "version": 2, "inherits": "{{inherits}}", "metadata": { "visible": true, "manufacturer": "{{make}}" } }""";

    [Fact]
    public void Cura_SkipsAndLogsMalformedDuplicateKeyOrphanedAndCyclicDefinitions()
    {
        using var tree = new ProfileTree();
        File.Copy(Path.Combine(CuraFixture, FdmBase, "fdmprinter.def.json"), Path.Combine(Directory.CreateDirectory(Path.Combine(tree.Root, FdmBase)).FullName, "fdmprinter.def.json"));
        tree.Write(FdmBase + "acme_good.def.json", CuraPrinter("Acme Good", "fdmprinter"))
            .Write(FdmBase + "acme_broken.def.json", "{ \"name\": \"Acme Broken\", ")
            .Write(FdmBase + "acme_dupe.def.json", """{ "name": "Acme Dupe", "inherits": "fdmprinter", "a": 1, "a": 2 }""")
            .Write(FdmBase + "acme_orphan.def.json", CuraPrinter("Acme Orphan", "acme_nowhere"))
            .Write(FdmBase + "acme_loop_a.def.json", CuraPrinter("Acme Loop A", "acme_loop_b"))
            .Write(FdmBase + "acme_loop_b.def.json", CuraPrinter("Acme Loop B", "acme_loop_a"));
        var log = new List<string>();

        var models = CuraProfileReader.Read(tree.Root, CatalogNormalizer.NoAliases, log.Add);

        Assert.Equal(["Good"], models.Select(m => m.Model));
        Assert.Contains(log, l => l.Contains("acme_broken.def.json", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("acme_dupe.def.json", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("acme_nowhere", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("cycle", StringComparison.Ordinal));
    }

    [Fact]
    public void Orca_SkipsAndLogsBrokenFiles_LogsAModelWithNoPreset_AndAllowlistsKinematics()
    {
        using var tree = new ProfileTree()
            .OrcaVendor("Acme", ["Acme Good", "Acme Odd"], structure: "weird")
            .Write("resources/profiles/Broken.json", "{ \"name\": ")
            .Write("resources/profiles/Beta.json", """
                {
                  "name": "Beta",
                  "machine_model_list": [
                    { "name": "Beta Lonely", "sub_path": "machine/Beta Lonely.json" },
                    { "name": "Beta Orphan", "sub_path": "machine/Beta Orphan.json" },
                    { "name": "Beta Loop", "sub_path": "machine/Beta Loop.json" },
                    { "name": "Beta Dupe", "sub_path": "machine/Beta Dupe.json" }
                  ],
                  "machine_list": [
                    { "name": "Beta Orphan 0.4 nozzle", "sub_path": "machine/Beta Orphan 0.4 nozzle.json" },
                    { "name": "loop_a", "sub_path": "machine/loop_a.json" },
                    { "name": "loop_b", "sub_path": "machine/loop_b.json" },
                    { "name": "Beta Loop 0.4 nozzle", "sub_path": "machine/Beta Loop 0.4 nozzle.json" },
                    { "name": "Beta Dupe 0.4 nozzle", "sub_path": "machine/Beta Dupe 0.4 nozzle.json" }
                  ]
                }
                """)
            .Write("resources/profiles/Beta/machine/Beta Orphan 0.4 nozzle.json",
                """{ "type": "machine", "name": "Beta Orphan 0.4 nozzle", "inherits": "fdm_nowhere", "printer_model": "Beta Orphan", "printer_variant": "0.4" }""")
            .Write("resources/profiles/Beta/machine/loop_a.json", """{ "type": "machine", "name": "loop_a", "inherits": "loop_b" }""")
            .Write("resources/profiles/Beta/machine/loop_b.json", """{ "type": "machine", "name": "loop_b", "inherits": "loop_a" }""")
            .Write("resources/profiles/Beta/machine/Beta Loop 0.4 nozzle.json",
                """{ "type": "machine", "name": "Beta Loop 0.4 nozzle", "inherits": "loop_a", "printer_model": "Beta Loop", "printer_variant": "0.4" }""")
            .Write("resources/profiles/Beta/machine/Beta Dupe 0.4 nozzle.json",
                """{ "type": "machine", "name": "Beta Dupe 0.4 nozzle", "printer_model": "Beta Dupe", "printer_variant": "0.4", "printable_height": "1", "printable_height": "2" }""");
        var log = new List<string>();

        var models = OrcaProfileReader.Read(tree.Root, CatalogNormalizer.NoAliases, log.Add);

        // A model without a usable preset is still listed, with what its model file says.
        Assert.Equal(
            ["Acme|Good", "Acme|Odd", "Beta|Dupe", "Beta|Lonely", "Beta|Loop", "Beta|Orphan"],
            models.Select(m => $"{m.Make}|{m.Model}").Order(StringComparer.Ordinal));
        Assert.All(models.Where(m => m.Make == "Beta"), m => Assert.Null(m.BedWidthMm));
        // printer_structure "weird" is not a kinematics we know.
        Assert.Null(Assert.Single(models, m => m.Model == "Good").Kinematics);

        Assert.Contains(log, l => l.Contains("Broken.json", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("Beta Lonely", StringComparison.Ordinal) && l.Contains("no preset", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("fdm_nowhere", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("cycle", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("Beta Dupe 0.4 nozzle.json", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("cura")]
    [InlineData("orca")]
    public void AMissingSourceRoot_FailsTheRun(string source)
    {
        var missing = Path.Combine(AppContext.BaseDirectory, "no-such-checkout-" + Guid.NewGuid().ToString("N"));
        var inputs = source == "cura" ? new CatalogInputs { CuraDir = missing } : new CatalogInputs { OrcaDir = missing };

        var e = Assert.Throws<InvalidDataException>(() => PrinterCatalogGenerator.Generate(inputs));

        Assert.Contains(missing, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACuraSourceWithNoPrinters_FailsTheRun()
    {
        using var tree = new ProfileTree();
        File.Copy(Path.Combine(CuraFixture, FdmBase, "fdmprinter.def.json"), Path.Combine(Directory.CreateDirectory(Path.Combine(tree.Root, FdmBase)).FullName, "fdmprinter.def.json"));

        var e = Assert.Throws<InvalidDataException>(() => PrinterCatalogGenerator.Generate(new CatalogInputs { CuraDir = tree.Root }));

        Assert.Contains("no printer models", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOrcaSourceWithNoPrinters_FailsTheRun()
    {
        using var tree = new ProfileTree().Write("resources/profiles/Custom.json", """{ "name": "Custom", "machine_model_list": [] }""");

        var e = Assert.Throws<InvalidDataException>(() => PrinterCatalogGenerator.Generate(new CatalogInputs { OrcaDir = tree.Root }));

        Assert.Contains("no printer models", e.Message, StringComparison.Ordinal);
    }
}
