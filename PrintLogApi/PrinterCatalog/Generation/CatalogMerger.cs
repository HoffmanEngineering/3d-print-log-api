using System.Text.Json.Nodes;

namespace PrintLogApi.PrinterCatalog.Generation;

/// <summary>
/// Joins the two sources on <see cref="CatalogNormalizer.Key"/> (OrcaSlicer wins a field both
/// have: its printable area is the usable bed, where Cura often stores the machine size), then
/// applies <c>catalog-overrides.json</c>. Every spec field records where it came from.
/// </summary>
public static class CatalogMerger
{
    public const string OverrideSource = "override";

    /// <summary>Records <paramref name="source"/> for every spec field the model has a value for.</summary>
    public static CatalogModel Stamp(CatalogModel model, string source)
    {
        var sources = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (field, hasValue) in Fields(model))
        {
            if (hasValue)
            {
                sources[field] = source;
            }
        }
        return model with { Sources = sources };
    }

    /// <summary>
    /// One model per key. Within a source the first model for a key wins (readers return them in
    /// file order) and later ones are logged; across sources Orca's names and values win.
    /// </summary>
    public static IReadOnlyList<CatalogModel> Merge(
        IEnumerable<CatalogModel> cura, IEnumerable<CatalogModel> orca, CatalogNormalizer normalizer, Action<string>? log = null)
    {
        var curaByKey = ByKey(cura, normalizer, "cura", log);
        var orcaByKey = ByKey(orca, normalizer, "orca", log);

        var merged = new List<CatalogModel>();
        foreach (var (key, c) in curaByKey)
        {
            merged.Add(orcaByKey.TryGetValue(key, out var o) ? Combine(o, c) : c);
        }
        merged.AddRange(orcaByKey.Where(o => !curaByKey.ContainsKey(o.Key)).Select(o => o.Value));
        return merged;
    }

    private static Dictionary<string, CatalogModel> ByKey(
        IEnumerable<CatalogModel> models, CatalogNormalizer normalizer, string source, Action<string>? log)
    {
        var byKey = new Dictionary<string, CatalogModel>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            var key = normalizer.Key(model.Make, model.Model);
            if (!byKey.TryAdd(key, model))
            {
                // The duplicate's identity still points at the kept model, so its id survives
                // whichever of the two files disappears first.
                byKey[key] = byKey[key] with { SourceKeys = UnionKeys(byKey[key].SourceKeys, model.SourceKeys) };
                log?.Invoke($"{source}: duplicate key, keeping the first: {model.Make} | {model.Model}");
            }
        }
        return byKey;
    }

    /// <summary>Field by field: the preferred model's value when it has one, else the other's.</summary>
    private static CatalogModel Combine(CatalogModel preferred, CatalogModel other)
    {
        var sources = new SortedDictionary<string, string>(StringComparer.Ordinal);

        T Pick<T>(string field, T fromPreferred, T fromOther)
        {
            var (value, from) = fromPreferred is not null ? (fromPreferred, preferred) : (fromOther, other);
            if (value is not null && from.Sources?.TryGetValue(field, out var source) == true)
            {
                sources[field] = source;
            }
            return value;
        }

        return preferred with
        {
            Technology = Pick("technology", preferred.Technology, other.Technology),
            BedWidthMm = Pick("bedWidthMm", preferred.BedWidthMm, other.BedWidthMm),
            BedDepthMm = Pick("bedDepthMm", preferred.BedDepthMm, other.BedDepthMm),
            BedHeightMm = Pick("bedHeightMm", preferred.BedHeightMm, other.BedHeightMm),
            NozzleOptionsMm = Pick("nozzleOptionsMm", preferred.NozzleOptionsMm, other.NozzleOptionsMm),
            DefaultNozzleMm = Pick("defaultNozzleMm", preferred.DefaultNozzleMm, other.DefaultNozzleMm),
            FilamentDiameterMm = Pick("filamentDiameterMm", preferred.FilamentDiameterMm, other.FilamentDiameterMm),
            HasHeatedBed = Pick("hasHeatedBed", preferred.HasHeatedBed, other.HasHeatedBed),
            HasHeatedChamber = Pick("hasHeatedChamber", preferred.HasHeatedChamber, other.HasHeatedChamber),
            ExtruderCount = Pick("extruderCount", preferred.ExtruderCount, other.ExtruderCount),
            DefaultSlotCount = Pick("defaultSlotCount", preferred.DefaultSlotCount, other.DefaultSlotCount),
            Kinematics = Pick("kinematics", preferred.Kinematics, other.Kinematics),
            SourceKeys = UnionKeys(preferred.SourceKeys, other.SourceKeys),
            Sources = sources,
        };
    }

    /// <summary>
    /// Applies <c>catalog-overrides.json</c>, an object keyed by catalog id. An entry for a
    /// generated id corrects its fields (recorded as <c>override</c>), adds <c>aliases</c>, sets
    /// <c>featured</c>, or drops it with <c>hidden: true</c>. An entry for an unknown id that names
    /// a <c>make</c> and <c>model</c> is a whole extra model; one that doesn't is stale and logged.
    /// So is any entry for a <paramref name="reservedIds">reserved</paramref> id: that id belongs
    /// to a generated model that is gone for now, and an extra entry would take it over.
    /// </summary>
    public static IReadOnlyList<CatalogModel> ApplyOverrides(
        IReadOnlyList<CatalogModel> models, JsonObject overrides, Action<string>? log = null, IReadOnlySet<string>? reservedIds = null)
    {
        var byId = models.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var result = new List<CatalogModel>(models);
        var hidden = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (id, node) in overrides)
        {
            if (node is not JsonObject entry)
            {
                throw new InvalidDataException($"catalog-overrides.json: \"{id}\" must be an object.");
            }
            if (ProfileValues.AsBool(entry["hidden"]) == true)
            {
                hidden.Add(id);
            }
            if (byId.TryGetValue(id, out var existing))
            {
                result[result.IndexOf(existing)] = Override(existing, id, entry);
            }
            else if (reservedIds?.Contains(id) != true
                && ProfileValues.AsString(entry["make"]) is { Length: > 0 }
                && ProfileValues.AsString(entry["model"]) is { Length: > 0 })
            {
                result.Add(Override(new CatalogModel { Id = id, Sources = new SortedDictionary<string, string>(StringComparer.Ordinal) }, id, entry));
            }
            else if (reservedIds?.Contains(id) == true)
            {
                log?.Invoke(hidden.Contains(id)
                    ? $"catalog-overrides.json: \"{id}\" hides a retired id; nothing to hide until its model returns"
                    : $"catalog-overrides.json: \"{id}\" is a retired id; its corrections wait until its model returns");
            }
            else
            {
                log?.Invoke($"catalog-overrides.json: \"{id}\" matches no model and is not a whole entry; ignored");
            }
        }

        return result.Where(m => !hidden.Contains(m.Id)).ToList();
    }

    private static CatalogModel Override(CatalogModel model, string id, JsonObject entry)
    {
        var sources = new SortedDictionary<string, string>(model.Sources?.ToDictionary() ?? [], StringComparer.Ordinal);
        var m = model;

        T Set<T>(string field, T value)
        {
            if (field != "make" && field != "model")
            {
                sources[field] = OverrideSource;
            }
            return value;
        }

        double Number(string field, JsonNode? v) => ProfileValues.AsDouble(v) ?? throw Invalid(id, field, "a number");
        int Integer(string field, JsonNode? v) => ProfileValues.AsInt(v) ?? throw Invalid(id, field, "an integer");
        bool Boolean(string field, JsonNode? v) => ProfileValues.AsBool(v) ?? throw Invalid(id, field, "true or false");
        string Text(string field, JsonNode? v) => ProfileValues.AsString(v) is { Length: > 0 } s ? s : throw Invalid(id, field, "a string");

        foreach (var (field, v) in entry)
        {
            m = field switch
            {
                "make" => m with { Make = Set(field, Text(field, v)) },
                "model" => m with { Model = Set(field, Text(field, v)) },
                "technology" => m with { Technology = Set(field, Text(field, v) is "FFF" or "Resin" ? Text(field, v) : throw Invalid(id, field, "FFF or Resin")) },
                "bedWidthMm" => m with { BedWidthMm = Set(field, Number(field, v)) },
                "bedDepthMm" => m with { BedDepthMm = Set(field, Number(field, v)) },
                "bedHeightMm" => m with { BedHeightMm = Set(field, Number(field, v)) },
                "nozzleOptionsMm" => m with
                {
                    NozzleOptionsMm = Set(field, (v as JsonArray ?? throw Invalid(id, field, "an array")).Select(n => Number(field, n)).Distinct().Order().ToList()),
                },
                "defaultNozzleMm" => m with { DefaultNozzleMm = Set(field, Number(field, v)) },
                "filamentDiameterMm" => m with { FilamentDiameterMm = Set(field, Number(field, v)) },
                "hasHeatedBed" => m with { HasHeatedBed = Set(field, Boolean(field, v)) },
                "hasHeatedChamber" => m with { HasHeatedChamber = Set(field, Boolean(field, v)) },
                "extruderCount" => m with { ExtruderCount = Set(field, Integer(field, v)) },
                "defaultSlotCount" or "slotCount" => m with { DefaultSlotCount = Set("defaultSlotCount", Integer(field, v)) },
                "kinematics" => m with { Kinematics = Set(field, Text(field, v)) },
                "aliases" => m with
                {
                    Aliases = (m.Aliases ?? [])
                        .Concat((v as JsonArray ?? throw Invalid(id, field, "an array")).Select(n => Text(field, n)))
                        .Distinct(StringComparer.Ordinal)
                        .ToList(),
                },
                "featured" => m with { Featured = Integer(field, v) },
                "hidden" => m,
                // For reviewers of catalog-overrides.json (why a value differs from the slicer); never written.
                "note" => m,
                _ => throw new InvalidDataException($"catalog-overrides.json: \"{id}\" has unknown field \"{field}\"."),
            };
        }
        return m with { Sources = sources };
    }

    /// <summary>Sorted, distinct, ordinal.</summary>
    public static IReadOnlyList<string>? UnionKeys(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        var keys = (a ?? []).Concat(b ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return keys.Count == 0 ? null : keys;
    }

    private static InvalidDataException Invalid(string id, string field, string expected) =>
        new($"catalog-overrides.json: \"{id}\".{field} must be {expected}.");

    private static IEnumerable<(string Field, bool HasValue)> Fields(CatalogModel m)
    {
        yield return ("bedWidthMm", m.BedWidthMm is not null);
        yield return ("bedDepthMm", m.BedDepthMm is not null);
        yield return ("bedHeightMm", m.BedHeightMm is not null);
        yield return ("nozzleOptionsMm", m.NozzleOptionsMm is not null);
        yield return ("defaultNozzleMm", m.DefaultNozzleMm is not null);
        yield return ("filamentDiameterMm", m.FilamentDiameterMm is not null);
        yield return ("hasHeatedBed", m.HasHeatedBed is not null);
        yield return ("hasHeatedChamber", m.HasHeatedChamber is not null);
        yield return ("extruderCount", m.ExtruderCount is not null);
        yield return ("defaultSlotCount", m.DefaultSlotCount is not null);
        yield return ("kinematics", m.Kinematics is not null);
    }
}
