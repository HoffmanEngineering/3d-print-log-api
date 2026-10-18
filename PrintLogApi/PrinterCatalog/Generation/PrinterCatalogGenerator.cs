using System.Text.Json;
using System.Text.Json.Nodes;

namespace PrintLogApi.PrinterCatalog.Generation;

/// <summary>Everything one generator run reads. Directories are checkouts of the source repos.</summary>
public sealed record CatalogInputs
{
    /// <summary>A Cura checkout (the folder holding <c>resources/definitions</c>), or null to skip Cura.</summary>
    public string? CuraDir { get; init; }

    /// <summary>An OrcaSlicer checkout (the folder holding <c>resources/profiles</c>), or null to skip Orca.</summary>
    public string? OrcaDir { get; init; }

    /// <summary>The repos, tags and commits the checkouts came from, copied into the file.</summary>
    public IReadOnlyList<CatalogSource> Sources { get; init; } = [];

    /// <summary><c>make-aliases.json</c>: raw make to canonical make.</summary>
    public IReadOnlyDictionary<string, string> MakeAliases { get; init; } = new Dictionary<string, string>();

    /// <summary><c>catalog-overrides.json</c>, keyed by catalog id.</summary>
    public JsonObject Overrides { get; init; } = [];

    /// <summary>The last published <c>printer-catalog.json</c>, whose ids are kept.</summary>
    public CatalogFile? Previous { get; init; }

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>Warnings: unreadable files, duplicates, stale overrides.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// Builds <c>printer-catalog.json</c> (spec §5.1): read both sources, merge on the normalized
/// key, keep published ids, apply the overrides, sort by make then model, and version the result
/// by the hash of its models.
/// </summary>
public static class PrinterCatalogGenerator
{
    public static CatalogFile Generate(CatalogInputs inputs)
    {
        if (inputs.CuraDir is null && inputs.OrcaDir is null)
        {
            throw new InvalidDataException("no source is configured; catalog-sources.json must declare Cura, OrcaSlicer or both");
        }
        var normalizer = new CatalogNormalizer(inputs.MakeAliases);

        var cura = ReadSource("cura", inputs.CuraDir, dir => CuraProfileReader.Read(dir, normalizer, inputs.Log));
        var orca = ReadSource("orca", inputs.OrcaDir, dir => OrcaProfileReader.Read(dir, normalizer, inputs.Log));

        var merged = CatalogMerger.Merge(cura, orca, normalizer, inputs.Log);
        var withIds = CatalogIds.Assign(merged, inputs.Previous, normalizer, inputs.Log);

        // Ids a generated model held before but that no source produces now. An override for one
        // is stale; it must not turn into an extra entry that takes the id over.
        var generatedIds = withIds.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var dormantIds = (inputs.Previous?.Models ?? []).Where(m => m.SourceKeys is not null).Select(m => m.Id)
            .Concat((inputs.Previous?.Reserved ?? []).Where(r => r.SourceKeys is not null).Select(r => r.Id))
            .Where(id => !generatedIds.Contains(id))
            .ToHashSet(StringComparer.Ordinal);

        LogNewIdCollisions(inputs, withIds, normalizer);

        var models = CatalogMerger.ApplyOverrides(withIds, inputs.Overrides, inputs.Log, dormantIds)
            .OrderBy(m => m.Make, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Model, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .ToList();

        var reserved = Reserved(inputs.Previous, withIds, models);
        var version = CatalogFile.ComputeVersion(models);

        // An unchanged catalog keeps its timestamp, so a refresh that found nothing new leaves
        // the committed file byte-identical and opens no PR.
        var unchanged = inputs.Previous is { } previous
            && previous.Version == version
            && previous.Sources.SequenceEqual(inputs.Sources)
            // Records holding lists compare by reference, so compare what would be written.
            && JsonSerializer.Serialize(previous.Reserved, CatalogFile.JsonOptions) == JsonSerializer.Serialize(reserved, CatalogFile.JsonOptions);

        return new CatalogFile
        {
            Version = version,
            GeneratedAt = unchanged ? inputs.Previous!.GeneratedAt : inputs.Clock.GetUtcNow(),
            Attribution = CatalogFile.AttributionText,
            Sources = inputs.Sources,
            Models = models,
            Reserved = reserved,
        };
    }

    /// <summary>
    /// An override extra (make and model given) whose id a brand-new source model took this run,
    /// under different names. Accepted edge case: the entry is applied to that model as
    /// corrections, so say so.
    /// </summary>
    private static void LogNewIdCollisions(CatalogInputs inputs, IReadOnlyList<CatalogModel> generated, CatalogNormalizer normalizer)
    {
        if (inputs.Log is null)
        {
            return;
        }
        var issued = (inputs.Previous?.Models ?? []).Select(m => m.Id)
            .Concat((inputs.Previous?.Reserved ?? []).Select(r => r.Id))
            .ToHashSet(StringComparer.Ordinal);
        var byId = generated.ToDictionary(m => m.Id, StringComparer.Ordinal);
        foreach (var (id, node) in inputs.Overrides)
        {
            if (issued.Contains(id)
                || !byId.TryGetValue(id, out var model)
                || node is not JsonObject entry
                || ProfileValues.AsString(entry["make"]) is not { Length: > 0 } make
                || ProfileValues.AsString(entry["model"]) is not { Length: > 0 } name
                || normalizer.Key(make, name) == normalizer.Key(model.Make, model.Model))
            {
                continue;
            }
            inputs.Log(
                $"catalog-overrides.json: \"{id}\" (an extra, {make} | {name}) collides with the new source model " +
                $"{model.Make} | {model.Model}, which took that id; its fields are applied to that model as corrections");
        }
    }

    /// <summary>A configured source must exist and produce models; an empty one fails the run (R3).</summary>
    private static IReadOnlyList<CatalogModel> ReadSource(string name, string? dir, Func<string, IReadOnlyList<CatalogModel>> read)
    {
        if (dir is null)
        {
            return [];
        }
        var models = read(dir);
        if (models.Count == 0)
        {
            throw new InvalidDataException($"{name}: {dir} yielded no printer models; is it the right checkout?");
        }
        return models;
    }

    /// <summary>
    /// Every id ever issued that is not visible now: the previous reserved list, the previous
    /// models, and this run's hidden ones, each with its latest known source key. Sorted by id.
    /// </summary>
    private static IReadOnlyList<ReservedId> Reserved(CatalogFile? previous, IReadOnlyList<CatalogModel> generated, IReadOnlyList<CatalogModel> visible)
    {
        var keysById = new SortedDictionary<string, IReadOnlyList<string>?>(StringComparer.Ordinal);
        var mergedInto = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in previous?.Reserved ?? [])
        {
            keysById[r.Id] = r.SourceKeys;
            if (r.MergedInto is not null)
            {
                mergedInto[r.Id] = r.MergedInto;
            }
        }
        foreach (var m in previous?.Models ?? [])
        {
            keysById[m.Id] = m.SourceKeys;
        }
        foreach (var m in generated)
        {
            keysById[m.Id] = m.SourceKeys;
        }
        foreach (var m in visible)
        {
            keysById.Remove(m.Id);
        }

        // An id that lost its keys to another generated model was merged into it. An existing
        // pointer stays: it names the direct successor, and the chain is the consumer's to follow.
        // An id generated this run (hidden, since visible ids are not reserved) has its own model,
        // so it is merged into nothing and an old pointer is dropped.
        var generatedIds = generated.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var idByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in generated)
        {
            foreach (var key in m.SourceKeys ?? [])
            {
                idByKey.TryAdd(key, m.Id);
            }
        }
        string? SuccessorOf(string id, IReadOnlyList<string>? keys) =>
            generatedIds.Contains(id)
                ? null
                : mergedInto.GetValueOrDefault(id)
                    ?? (keys ?? []).Select(k => idByKey.GetValueOrDefault(k)).Where(s => s is not null && s != id).Order(StringComparer.Ordinal).FirstOrDefault();

        var reserved = keysById.Select(r => new ReservedId { Id = r.Key, SourceKeys = r.Value, MergedInto = SuccessorOf(r.Key, r.Value) }).ToList();
        ThrowOnMergedIntoCycle(reserved);
        return reserved;
    }

    /// <summary>
    /// Following <c>mergedInto</c> from any reserved id must end, at a visible id or at a reserved
    /// one with no successor. A cycle would send the seeder round forever, so it fails the run.
    /// </summary>
    private static void ThrowOnMergedIntoCycle(IReadOnlyList<ReservedId> reserved)
    {
        var next = reserved.Where(r => r.MergedInto is not null).ToDictionary(r => r.Id, r => r.MergedInto!, StringComparer.Ordinal);
        foreach (var start in next.Keys)
        {
            var visited = new List<string>();
            for (string? id = start; id is not null; id = next.GetValueOrDefault(id))
            {
                if (visited.Contains(id, StringComparer.Ordinal))
                {
                    throw new InvalidDataException(
                        $"reserved: mergedInto forms a cycle ({string.Join(" -> ", visited.Append(id))}); fix the reserved entries in the previous file");
                }
                visited.Add(id);
            }
        }
    }
}
