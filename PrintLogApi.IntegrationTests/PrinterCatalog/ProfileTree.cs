using System.Text.Json.Nodes;

namespace PrintLogApi.IntegrationTests.PrinterCatalog;

/// <summary>
/// A throwaway Cura or Orca checkout under the test output folder, for the cases the static
/// fixtures can't hold: sources that change between generations, and broken files.
/// </summary>
internal sealed class ProfileTree : IDisposable
{
    public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "tmp-profile-trees", Guid.NewGuid().ToString("N"));

    public ProfileTree() => Directory.CreateDirectory(Root);

    public ProfileTree CopyFrom(string source)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(Root, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return this;
    }

    public ProfileTree Write(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return this;
    }

    /// <summary>
    /// An Orca vendor whose every model has a machine_model file and one 0.4 preset with a
    /// 200 x 200 x 200 bed. <paramref name="structure"/> becomes each preset's printer_structure.
    /// </summary>
    public ProfileTree OrcaVendor(string vendorName, IReadOnlyList<string> modelNames, string structure = "corexy")
    {
        var models = new JsonArray();
        var machines = new JsonArray();
        foreach (var name in modelNames)
        {
            models.Add(new JsonObject { ["name"] = name, ["sub_path"] = $"machine/{name}.json" });
            machines.Add(new JsonObject { ["name"] = $"{name} 0.4 nozzle", ["sub_path"] = $"machine/{name} 0.4 nozzle.json" });
            Write($"resources/profiles/{vendorName}/machine/{name}.json", new JsonObject
            {
                ["type"] = "machine_model",
                ["name"] = name,
                ["nozzle_diameter"] = "0.4",
                ["machine_tech"] = "FFF",
            }.ToJsonString());
            Write($"resources/profiles/{vendorName}/machine/{name} 0.4 nozzle.json", new JsonObject
            {
                ["type"] = "machine",
                ["name"] = $"{name} 0.4 nozzle",
                ["instantiation"] = "true",
                ["printer_model"] = name,
                ["printer_variant"] = "0.4",
                ["printer_structure"] = structure,
                ["nozzle_diameter"] = new JsonArray("0.4"),
                ["printable_area"] = new JsonArray("0x0", "200x0", "200x200", "0x200"),
                ["printable_height"] = "200",
            }.ToJsonString());
        }
        return Write($"resources/profiles/{vendorName}.json", new JsonObject
        {
            ["name"] = vendorName,
            ["machine_model_list"] = models,
            ["machine_list"] = machines,
        }.ToJsonString());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Left under bin/; the next build's output folder is not affected.
        }
    }
}
