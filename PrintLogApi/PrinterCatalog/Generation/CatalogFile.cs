using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrintLogApi.PrinterCatalog.Generation;

/// <summary>
/// <c>printer-catalog.json</c> (spec §5.1). Facts only: names, the §5.2 fields, where each came
/// from, and the attribution. Never a copy of a profile file.
/// </summary>
public sealed record CatalogFile
{
    public const string AttributionText =
        "Printer specifications derived from Ultimaker Cura (LGPL-3.0) and OrcaSlicer (AGPL-3.0) printer profiles.";

    /// <summary>The SHA-256 of the serialized <see cref="Models"/>, lowercase hex.</summary>
    public string Version { get; init; } = "";

    public DateTimeOffset GeneratedAt { get; init; }

    public string Attribution { get; init; } = AttributionText;

    public IReadOnlyList<CatalogSource> Sources { get; init; } = [];

    public IReadOnlyList<CatalogModel> Models { get; init; } = [];

    /// <summary>
    /// Every id ever issued that is not a visible model now: retired (gone from the sources) or
    /// hidden by an override. Kept forever, sorted by id, so no other model can take one. The
    /// seeder and the UI ignore it.
    /// </summary>
    public IReadOnlyList<ReservedId> Reserved { get; init; } = [];

    /// <summary>
    /// camelCase, unknown fields omitted, and LF line endings whatever the OS, so the file and its
    /// version hash come out byte-identical on a workstation and on CI. The relaxed encoder keeps
    /// names like "MINI+" readable in review diffs; the file is never embedded in HTML.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(CatalogFile file) => JsonSerializer.Serialize(file, JsonOptions) + "\n";

    public static CatalogFile Deserialize(string json) =>
        JsonSerializer.Deserialize<CatalogFile>(json, JsonOptions)
        ?? throw new InvalidDataException("The printer catalog file is empty.");

    public static string ComputeVersion(IReadOnlyList<CatalogModel> models) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(models, JsonOptions))));
}

/// <summary>One upstream profile repository the facts were read from.</summary>
public sealed record CatalogSource
{
    public string Repo { get; init; } = "";
    public string Tag { get; init; } = "";
    public string? Commit { get; init; }
    public string? License { get; init; }
}

/// <summary>An id no new model may take, with the source key that reclaims it.</summary>
public sealed record ReservedId
{
    public string Id { get; init; } = "";

    /// <summary>Null for an id that was a whole extra entry from the overrides.</summary>
    public string? SourceKey { get; init; }
}

/// <summary>One printer model. Every spec field is optional, and an unknown one is omitted.</summary>
public sealed record CatalogModel
{
    public string Id { get; init; } = "";

    /// <summary>
    /// The normalized key of the source make and model, before overrides. Ids follow it, so a
    /// rename in the sources or in the overrides never changes an id. Null for an extra entry.
    /// </summary>
    public string? SourceKey { get; init; }

    public string Make { get; init; } = "";
    public string Model { get; init; } = "";
    public IReadOnlyList<string>? Aliases { get; init; }

    /// <summary><c>FFF</c> or <c>Resin</c>.</summary>
    public string Technology { get; init; } = "FFF";

    public double? BedWidthMm { get; init; }
    public double? BedDepthMm { get; init; }
    public double? BedHeightMm { get; init; }
    public IReadOnlyList<double>? NozzleOptionsMm { get; init; }
    public double? DefaultNozzleMm { get; init; }
    public double? FilamentDiameterMm { get; init; }
    public bool? HasHeatedBed { get; init; }
    public bool? HasHeatedChamber { get; init; }
    public int? ExtruderCount { get; init; }
    public int? DefaultSlotCount { get; init; }
    public string? Kinematics { get; init; }

    /// <summary>Position in the popular list's fallback, from the overrides.</summary>
    public int? Featured { get; init; }

    /// <summary>Field name to <c>cura</c>, <c>orca</c> or <c>override</c>, sorted by field.</summary>
    public IReadOnlyDictionary<string, string>? Sources { get; init; }
}
