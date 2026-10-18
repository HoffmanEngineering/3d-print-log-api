using System.Text.Json.Nodes;

namespace PrintLogApi.PrinterCatalog.Generation;

/// <summary>
/// Reads printer facts out of an OrcaSlicer checkout (spec §5.1, "Orca extraction"), porting the
/// UI's old <c>orca-machine-def-parser/parser.ts</c>. Each vendor's
/// <c>resources/profiles/&lt;Vendor&gt;.json</c> lists its <c>machine_model_list</c>; a model's
/// facts come from its <c>machine_model</c> file and from one machine preset, resolved through its
/// <c>inherits</c> chain inside the vendor folder. Only the §5.2 fields are read.
/// </summary>
public static class OrcaProfileReader
{
    public const string Source = "orca";

    private static readonly HashSet<string> SkippedFiles = new(StringComparer.OrdinalIgnoreCase) { "blacklist.json" };
    private static readonly HashSet<string> SkippedVendors = new(StringComparer.Ordinal) { "Custom" };

    // Orca's printer_structure values that name a kinematics; "undefine" and anything else don't.
    private static readonly HashSet<string> KnownKinematics = new(StringComparer.Ordinal) { "corexy", "i3", "hbot", "delta", "corexz" };

    public static IReadOnlyList<CatalogModel> Read(string orcaDir, CatalogNormalizer normalizer, Action<string>? log = null)
    {
        var profiles = Path.Combine(orcaDir, "resources", "profiles");
        if (!Directory.Exists(profiles))
        {
            throw new InvalidDataException($"orca: {orcaDir} has no resources/profiles folder; is it an OrcaSlicer checkout?");
        }
        var models = new List<CatalogModel>();
        foreach (var vendorFile in Directory.EnumerateFiles(profiles, "*.json").Order(StringComparer.Ordinal))
        {
            if (SkippedFiles.Contains(Path.GetFileName(vendorFile)) || ProfileValues.ReadObject(vendorFile, log) is not { } vendor)
            {
                continue;
            }
            var vendorName = ProfileValues.AsString(vendor["name"]) ?? "";
            var modelList = Entries(vendor["machine_model_list"]);
            if (modelList.Count == 0 || SkippedVendors.Contains(vendorName))
            {
                continue;
            }
            var vendorDir = Path.Combine(profiles, Path.GetFileNameWithoutExtension(vendorFile));
            models.AddRange(ReadVendor(vendorName, vendorDir, modelList, Entries(vendor["machine_list"]), normalizer, log));
        }
        return models;
    }

    private static IEnumerable<CatalogModel> ReadVendor(
        string vendorName,
        string vendorDir,
        IReadOnlyList<(string Name, string SubPath)> modelList,
        IReadOnlyList<(string Name, string SubPath)> machineList,
        CatalogNormalizer normalizer,
        Action<string>? log)
    {
        var prefix = DetectCommonPrefix(modelList.Select(m => m.Name).ToList(), vendorName);
        var make = normalizer.ApplyAlias(prefix.Length > 0 ? prefix : vendorName);

        var presets = new PresetResolver(vendorDir, machineList, log);
        var resolvedPresets = machineList
            .Select(m => presets.Resolve(m.Name))
            .OfType<JsonObject>()
            .Where(p => ProfileValues.AsString(p["type"]) == "machine" && ProfileValues.AsBool(p["instantiation"]) != false)
            .ToList();

        foreach (var (name, subPath) in modelList)
        {
            // Every name starts with the prefix's words (compared ignoring case), so cut by length.
            var model = prefix.Length > 0 ? name[prefix.Length..].Trim() : name;
            if (model.Length == 0)
            {
                continue;
            }
            var modelFile = ProfileValues.ReadObject(Path.Combine(vendorDir, subPath), log);
            var mine = resolvedPresets.Where(p => ProfileValues.AsString(p["printer_model"]) == name).ToList();
            var preset = mine.FirstOrDefault(p => ProfileValues.AsString(p["printer_variant"])?.Trim() == "0.4") ?? mine.FirstOrDefault();
            if (preset is null)
            {
                log?.Invoke($"orca: {vendorName} / {name} has no preset; listed without bed or nozzle facts");
            }
            // The vendor folder and machine_model name: raw, before the prefix cut and alias.
            yield return ReadModel($"{Source}:{Path.GetFileName(vendorDir)}/{name}", make, model, modelFile, preset);
        }
    }

    private static CatalogModel ReadModel(string sourceKey, string make, string model, JsonObject? modelFile, JsonObject? preset)
    {
        var options = (ProfileValues.AsString(modelFile?["nozzle_diameter"]) ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ProfileValues.ParseDouble)
            .OfType<double>()
            .Distinct()
            .Order()
            .ToList();

        var nozzles = (preset?["nozzle_diameter"] as JsonArray)?.Select(ProfileValues.AsDouble).ToList();
        var filaments = preset?["filament_diameter"] as JsonArray;
        var (width, depth) = BoundingBox(preset?["printable_area"]);
        var structure = ProfileValues.AsString(preset?["printer_structure"])?.Trim();

        return CatalogMerger.Stamp(new CatalogModel
        {
            SourceKeys = [sourceKey],
            Make = make,
            Model = model,
            Technology = Technology(ProfileValues.AsString(modelFile?["machine_tech"])),
            BedWidthMm = width,
            BedDepthMm = depth,
            BedHeightMm = ProfileValues.AsDouble(preset?["printable_height"]),
            NozzleOptionsMm = options.Count > 0 ? options : null,
            DefaultNozzleMm = nozzles?.FirstOrDefault(),
            ExtruderCount = nozzles is { Count: > 0 } ? nozzles.Count : null,
            FilamentDiameterMm = filaments is { Count: > 0 } ? ProfileValues.AsDouble(filaments[0]) : null,
            Kinematics = structure is not null && KnownKinematics.Contains(structure) ? structure : null,
        }, Source);
    }

    private static string Technology(string? machineTech) =>
        machineTech?.Trim().ToUpperInvariant() switch
        {
            "SLA" or "MSLA" or "DLP" or "LCD" => "Resin",
            _ => "FFF",
        };

    /// <summary>Width and depth of the bounding box of a polygon of <c>"XxY"</c> points.</summary>
    private static (double? Width, double? Depth) BoundingBox(JsonNode? area)
    {
        IEnumerable<string?> points = area switch
        {
            JsonArray array => array.Select(ProfileValues.AsString),
            JsonValue => (ProfileValues.AsString(area) ?? "").Split(','),
            _ => [],
        };
        var xs = new List<double>();
        var ys = new List<double>();
        foreach (var point in points)
        {
            var parts = point?.Split('x');
            if (parts is { Length: 2 } && ProfileValues.ParseDouble(parts[0]) is { } x && ProfileValues.ParseDouble(parts[1]) is { } y)
            {
                xs.Add(x);
                ys.Add(y);
            }
        }
        if (xs.Count < 3)
        {
            return (null, null);
        }
        return (Math.Round(xs.Max() - xs.Min(), 3), Math.Round(ys.Max() - ys.Min(), 3));
    }

    /// <summary>
    /// The longest leading word sequence every model name shares, never consuming a name's last
    /// word, and at most one word longer than the vendor's name. Ported from the old parser:
    /// <c>["Bambu Lab A1", "Bambu Lab P1P"]</c> under vendor "BBL" gives "Bambu Lab".
    /// </summary>
    public static string DetectCommonPrefix(IReadOnlyList<string> names, string vendorName)
    {
        if (names.Count == 0)
        {
            return "";
        }
        var split = names.Select(n => n.Split(' ')).ToList();
        var wordCap = vendorName.Split(' ').Length + 1;
        var maxPrefixLength = Math.Min(split.Min(n => n.Length) - 1, wordCap);

        var prefix = new List<string>();
        for (var i = 0; i < maxPrefixLength; i++)
        {
            var word = split[0][i];
            if (!split.All(n => string.Equals(n[i], word, StringComparison.OrdinalIgnoreCase)))
            {
                break;
            }
            prefix.Add(word);
        }
        return string.Join(' ', prefix);
    }

    private static List<(string Name, string SubPath)> Entries(JsonNode? list) =>
        (list as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(e => (Name: ProfileValues.AsString(e["name"]), SubPath: ProfileValues.AsString(e["sub_path"])))
            .Where(e => !string.IsNullOrWhiteSpace(e.Name) && !string.IsNullOrWhiteSpace(e.SubPath))
            .Select(e => (e.Name!, e.SubPath!))
            .ToList();

    /// <summary>
    /// Machine presets by name, with their <c>inherits</c> chain applied: a child's keys replace
    /// its parent's. Names resolve through the vendor's <c>machine_list</c>, falling back to
    /// <c>machine/&lt;name&gt;.json</c> for a common <c>fdm_*</c> base it doesn't list.
    /// </summary>
    private sealed class PresetResolver(string vendorDir, IReadOnlyList<(string Name, string SubPath)> machineList, Action<string>? log)
    {
        private readonly Dictionary<string, string> _paths = machineList
            .GroupBy(m => m.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().SubPath, StringComparer.Ordinal);

        private readonly Dictionary<string, JsonObject?> _resolved = new(StringComparer.Ordinal);

        public JsonObject? Resolve(string name) => Resolve(name, []);

        private JsonObject? Resolve(string name, HashSet<string> visiting)
        {
            if (_resolved.TryGetValue(name, out var cached))
            {
                return cached;
            }
            if (!visiting.Add(name))
            {
                log?.Invoke($"orca: inheritance cycle at {vendorDir}/{name}");
                return null;
            }
            var path = Path.Combine(vendorDir, _paths.GetValueOrDefault(name) ?? Path.Combine("machine", name + ".json"));
            var own = File.Exists(path) ? ProfileValues.ReadObject(path, log) : null;
            JsonObject? result = null;
            if (own is null)
            {
                log?.Invoke($"orca: missing preset {path}");
            }
            else if (ProfileValues.AsString(own["inherits"]) is { Length: > 0 } parentName)
            {
                var parent = Resolve(parentName, visiting);
                result = parent is null ? null : (JsonObject)parent.DeepClone();
                if (result is not null)
                {
                    foreach (var (key, value) in own)
                    {
                        result[key] = value?.DeepClone();
                    }
                }
            }
            else
            {
                result = own;
            }
            visiting.Remove(name);
            _resolved[name] = result;
            return result;
        }
    }
}
