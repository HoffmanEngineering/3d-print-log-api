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
        var normalizer = new CatalogNormalizer(inputs.MakeAliases);

        IReadOnlyList<CatalogModel> cura = inputs.CuraDir is null ? [] : CuraProfileReader.Read(inputs.CuraDir, normalizer, inputs.Log);
        IReadOnlyList<CatalogModel> orca = inputs.OrcaDir is null ? [] : OrcaProfileReader.Read(inputs.OrcaDir, normalizer, inputs.Log);

        var merged = CatalogMerger.Merge(cura, orca, normalizer, inputs.Log);
        var withIds = CatalogIds.Assign(merged, inputs.Previous, normalizer);
        var models = CatalogMerger.ApplyOverrides(withIds, inputs.Overrides, inputs.Log)
            .OrderBy(m => m.Make, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Model, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .ToList();

        var version = CatalogFile.ComputeVersion(models);

        // An unchanged catalog keeps its timestamp, so a refresh that found nothing new leaves
        // the committed file byte-identical and opens no PR.
        var unchanged = inputs.Previous is { } previous
            && previous.Version == version
            && previous.Sources.SequenceEqual(inputs.Sources);

        return new CatalogFile
        {
            Version = version,
            GeneratedAt = unchanged ? inputs.Previous!.GeneratedAt : inputs.Clock.GetUtcNow(),
            Attribution = CatalogFile.AttributionText,
            Sources = inputs.Sources,
            Models = models,
        };
    }
}
