using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PrintLogApi.PrinterCatalog.Generation;

/// <summary>
/// Reads printer facts out of a Cura checkout (spec §5.1, "Cura extraction"), porting the UI's
/// old <c>cura-machine-def-parser/parser.ts</c>. Each <c>resources/definitions/*.def.json</c> is
/// deep-merged with its <c>inherits</c> chain, root first. Only the §5.2 fields are read; nothing
/// else in a definition (G-code, platform meshes, settings) leaves this class.
/// </summary>
public static partial class CuraProfileReader
{
    public const string Source = "cura";

    // Names the old parser skipped: the abstract bases every vendor tree starts from.
    private static readonly HashSet<string> SkippedNames = new(StringComparer.Ordinal)
    {
        "FDM Printer Base Description", "Extruder", "Creawsome Base Printer", "FFF printer",
    };

    public static IReadOnlyList<CatalogModel> Read(string curaDir, CatalogNormalizer normalizer, Action<string>? log = null)
    {
        var resources = Path.Combine(curaDir, "resources");
        var definitionsDir = Path.Combine(resources, "definitions");
        if (!Directory.Exists(definitionsDir))
        {
            throw new InvalidDataException($"cura: {curaDir} has no resources/definitions folder; is it a Cura checkout?");
        }
        var definitions = LoadDefinitions(definitionsDir, log);
        var extruders = LoadDefinitions(Path.Combine(resources, "extruders"), log);
        var variants = LoadVariants(Path.Combine(resources, "variants"));

        var models = new List<CatalogModel>();
        foreach (var id in definitions.Keys.Order(StringComparer.Ordinal))
        {
            if (id is "fdmprinter" or "fdmextruder")
            {
                continue;
            }
            var chain = Chain(id, definitions, extruders, log);
            if (chain is null)
            {
                continue;
            }
            var merged = Merge(chain.Select(c => c.Definition));
            if (ReadModel(chain, merged, extruders, definitions, variants, normalizer, log) is { } model)
            {
                models.Add(model);
            }
        }
        return models;
    }

    private static CatalogModel? ReadModel(
        IReadOnlyList<(string Id, JsonObject Definition)> chain,
        JsonObject merged,
        IReadOnlyDictionary<string, JsonObject> extruders,
        IReadOnlyDictionary<string, JsonObject> definitions,
        IReadOnlyList<Variant> variants,
        CatalogNormalizer normalizer,
        Action<string>? log)
    {
        var metadata = merged["metadata"] as JsonObject;
        var name = ProfileValues.AsString(merged["name"])?.Trim() ?? "";
        var rawMake = ProfileValues.AsString(metadata?["manufacturer"])?.Trim() ?? "";

        if (ProfileValues.AsBool(metadata?["visible"]) == false
            || ProfileValues.AsString(metadata?["type"]) == "extruder"
            || name.Length == 0
            || SkippedNames.Contains(name)
            || BaseWord().IsMatch(name)
            || rawMake is "" or "Custom" or "Unknown")
        {
            return null;
        }

        var cleanedMake = rawMake.Replace("B.V.", "").Replace("N.V.", "").Trim();
        var make = normalizer.ApplyAlias(cleanedMake);
        var model = StripMakePrefix(name, [rawMake, cleanedMake, make]);

        var settings = FlattenSettings(merged);
        var extruder = FirstExtruderOverrides(metadata, extruders, definitions, log);
        var (defaultVariantNozzle, variantOptions) = NozzleVariants(chain, metadata, variants);

        var nozzle = defaultVariantNozzle
            ?? ProfileValues.AsDouble(Resolve(extruder, "machine_nozzle_size"))
            ?? ProfileValues.AsDouble(Resolve(settings, "machine_nozzle_size"));

        return CatalogMerger.Stamp(new CatalogModel
        {
            // The definition id: raw, before the alias and prefix stripping above.
            SourceKeys = [$"{Source}:{chain[^1].Id}"],
            Make = make,
            Model = model,
            Technology = "FFF",
            BedWidthMm = ProfileValues.AsDouble(Resolve(settings, "machine_width")),
            BedDepthMm = ProfileValues.AsDouble(Resolve(settings, "machine_depth")),
            BedHeightMm = ProfileValues.AsDouble(Resolve(settings, "machine_height")),
            HasHeatedBed = ProfileValues.AsBool(Resolve(settings, "machine_heated_bed")),
            HasHeatedChamber = ProfileValues.AsBool(Resolve(settings, "machine_heated_build_volume")),
            ExtruderCount = ProfileValues.AsInt(Resolve(settings, "machine_extruder_count")),
            FilamentDiameterMm = ProfileValues.AsDouble(Resolve(extruder, "material_diameter"))
                ?? ProfileValues.AsDouble(Resolve(settings, "material_diameter")),
            DefaultNozzleMm = nozzle,
            NozzleOptionsMm = variantOptions,
        }, Source);
    }

    /// <summary>"Creality Ender-3" by "Creality3D" is "Ender-3"; a name that is only the make stays whole.</summary>
    private static string StripMakePrefix(string name, IEnumerable<string> makes)
    {
        foreach (var make in makes.Where(m => m.Length > 0).OrderByDescending(m => m.Length))
        {
            if (name.Length > make.Length
                && name.StartsWith(make, StringComparison.OrdinalIgnoreCase)
                && !char.IsLetterOrDigit(name[make.Length]))
            {
                var rest = name[make.Length..].Trim(' ', '-', '_');
                if (rest.Length > 0)
                {
                    return rest;
                }
            }
        }
        return name;
    }

    // --- Inheritance ---

    /// <summary>The definition and its ancestors, root first, or null when a parent is missing.</summary>
    private static List<(string Id, JsonObject Definition)>? Chain(
        string id,
        IReadOnlyDictionary<string, JsonObject> primary,
        IReadOnlyDictionary<string, JsonObject> secondary,
        Action<string>? log)
    {
        var chain = new List<(string, JsonObject)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (string? current = id; current is not null;)
        {
            if (!seen.Add(current))
            {
                log?.Invoke($"cura: inheritance cycle at {current}");
                return null;
            }
            if (!primary.TryGetValue(current, out var definition) && !secondary.TryGetValue(current, out definition))
            {
                log?.Invoke($"cura: {id} inherits missing definition {current}");
                return null;
            }
            chain.Add((current, definition));
            current = ProfileValues.AsString(definition["inherits"]);
        }
        chain.Reverse();
        return chain;
    }

    private static JsonObject Merge(IEnumerable<JsonObject> rootFirst)
    {
        var merged = new JsonObject();
        foreach (var definition in rootFirst)
        {
            MergeInto(merged, definition);
        }
        return merged;
    }

    /// <summary>The old parser's <c>mergeDeep</c>: objects merge key by key, anything else replaces.</summary>
    private static void MergeInto(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (value is JsonObject sourceObject && target[key] is JsonObject targetObject)
            {
                MergeInto(targetObject, sourceObject);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    // --- Settings ---

    /// <summary>
    /// Every setting in the merged <c>settings</c> tree (fdmprinter's categories and their
    /// children), with the merged <c>overrides</c> merged over it.
    /// </summary>
    private static Dictionary<string, JsonObject> FlattenSettings(JsonObject merged)
    {
        var settings = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (merged["settings"] is JsonObject tree)
        {
            Flatten(tree, settings);
        }
        ApplyOverrides(merged, settings);
        return settings;
    }

    private static void Flatten(JsonObject level, Dictionary<string, JsonObject> into)
    {
        foreach (var (key, value) in level)
        {
            if (value is not JsonObject setting)
            {
                continue;
            }
            into[key] = setting;
            if (setting["children"] is JsonObject children)
            {
                Flatten(children, into);
            }
        }
    }

    private static void ApplyOverrides(JsonObject merged, Dictionary<string, JsonObject> settings)
    {
        if (merged["overrides"] is not JsonObject overrides)
        {
            return;
        }
        foreach (var (key, value) in overrides)
        {
            if (value is not JsonObject change)
            {
                continue;
            }
            if (!settings.TryGetValue(key, out var setting))
            {
                setting = new JsonObject();
                settings[key] = setting;
            }
            MergeInto(setting, change);
        }
    }

    /// <summary>
    /// A setting's <c>value</c> when it is a plain number or boolean literal, otherwise its
    /// <c>default_value</c>. Expressions ("machine_width * 2") are never evaluated.
    /// </summary>
    private static JsonNode? Resolve(IReadOnlyDictionary<string, JsonObject>? settings, string key)
    {
        if (settings is null || !settings.TryGetValue(key, out var setting))
        {
            return null;
        }
        var value = setting["value"];
        return ProfileValues.IsLiteral(value) ? value : setting["default_value"];
    }

    // --- Extruders ---

    /// <summary>
    /// Extruder 0's own merged overrides. Its definition's <c>settings</c> tree is left out on
    /// purpose: fdmextruder's defaults are not the printer's, so only what the vendor's extruder
    /// file states counts.
    /// </summary>
    private static Dictionary<string, JsonObject>? FirstExtruderOverrides(
        JsonObject? metadata,
        IReadOnlyDictionary<string, JsonObject> extruders,
        IReadOnlyDictionary<string, JsonObject> definitions,
        Action<string>? log)
    {
        var trains = metadata?["machine_extruder_trains"] as JsonObject;
        var id = ProfileValues.AsString(trains?["0"]);
        if (id is null)
        {
            return null;
        }
        var chain = Chain(id, extruders, definitions, log);
        if (chain is null)
        {
            return null;
        }
        var overrides = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        ApplyOverrides(Merge(chain.Select(c => new JsonObject { ["overrides"] = c.Definition["overrides"]?.DeepClone() })), overrides);
        return overrides;
    }

    // --- Variants ---

    private sealed record Variant(string Definition, string Name, double Nozzle);

    /// <summary>
    /// The nozzle variants of the nearest definition in the chain that has any. The default is the
    /// one named by <c>metadata.preferred_variant_name</c>.
    /// </summary>
    private static (double? Default, IReadOnlyList<double>? Options) NozzleVariants(
        IReadOnlyList<(string Id, JsonObject Definition)> chain, JsonObject? metadata, IReadOnlyList<Variant> variants)
    {
        var preferred = ProfileValues.AsString(metadata?["preferred_variant_name"]);
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var own = variants.Where(v => v.Definition == chain[i].Id).ToList();
            if (own.Count == 0)
            {
                continue;
            }
            var options = own.Select(v => v.Nozzle).Distinct().Order().ToList();
            var chosen = own.FirstOrDefault(v => v.Name == preferred);
            return (chosen?.Nozzle, options);
        }
        return (null, null);
    }

    private static List<Variant> LoadVariants(string directory)
    {
        var variants = new List<Variant>();
        if (!Directory.Exists(directory))
        {
            return variants;
        }
        foreach (var path in Directory.EnumerateFiles(directory, "*.inst.cfg", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var ini = ReadIni(path);
            var definition = ini.GetValueOrDefault("general.definition");
            var name = ini.GetValueOrDefault("general.name");
            var hardware = ini.GetValueOrDefault("metadata.hardware_type") ?? "nozzle";
            var nozzle = ProfileValues.ParseDouble(ini.GetValueOrDefault("values.machine_nozzle_size"));
            if (definition is not null && name is not null && hardware == "nozzle" && nozzle is not null)
            {
                variants.Add(new Variant(definition, name, nozzle.Value));
            }
        }
        return variants;
    }

    /// <summary>A flat <c>section.key</c> view of an instance container (Cura's INI dialect).</summary>
    private static Dictionary<string, string> ReadIni(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var section = "";
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }
            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1].Trim();
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq > 0)
            {
                values[$"{section}.{line[..eq].Trim()}"] = line[(eq + 1)..].Trim();
            }
        }
        return values;
    }

    private static Dictionary<string, JsonObject> LoadDefinitions(string directory, Action<string>? log)
    {
        var definitions = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (!Directory.Exists(directory))
        {
            return definitions;
        }
        foreach (var path in Directory.EnumerateFiles(directory, "*.def.json"))
        {
            if (ProfileValues.ReadObject(path, log) is { } definition)
            {
                definitions[Path.GetFileName(path)[..^".def.json".Length]] = definition;
            }
        }
        return definitions;
    }

    // The old parser's /[Bb]ase/i: any name mentioning a base is an abstract parent.
    [GeneratedRegex("base", RegexOptions.IgnoreCase)]
    private static partial Regex BaseWord();
}
