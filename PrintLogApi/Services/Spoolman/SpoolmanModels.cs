using System.ComponentModel;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrintLogApi.Services.Spoolman;

// The Spoolman v0.27.0 wire models (spoolman/api/v1/models.py), as far as the read-only phase 1
// fills them (#177). Every member carries its JSON name explicitly, so the shape is pinned here and
// not by a naming policy, and every optional member is nullable and omitted when null, matching
// FastAPI's response_model_exclude_none: several clients treat a present null differently from an
// absent key. Required members (Spoolman's own non-optional fields) are never null.

/// <summary>A Spoolman vendor: one per distinct brand among the user's spools.</summary>
[ImmutableObject(true)]
public sealed record SpoolmanVendor(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("registered")] DateTime Registered,
    [property: JsonPropertyName("name")] string Name)
{
    [JsonPropertyName("comment")] public string? Comment { get; init; }
    [JsonPropertyName("empty_spool_weight")] public double? EmptySpoolWeight { get; init; }
    [JsonPropertyName("external_id")] public string? ExternalId { get; init; }
    [JsonPropertyName("extra")] public IReadOnlyDictionary<string, string> Extra { get; init; } = SpoolmanJson.NoExtra;
}

/// <summary>
/// A Spoolman filament (type). 3D Print Log has one entity per spool, so each spool yields its own
/// filament, sharing the spool's number as its id.
/// </summary>
[ImmutableObject(true)]
public sealed record SpoolmanFilament(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("registered")] DateTime Registered,
    [property: JsonPropertyName("density")] double Density,
    [property: JsonPropertyName("diameter")] double Diameter)
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("vendor")] public SpoolmanVendor? Vendor { get; init; }
    [JsonPropertyName("material")] public string? Material { get; init; }
    [JsonPropertyName("price")] public double? Price { get; init; }
    [JsonPropertyName("weight")] public double? Weight { get; init; }
    [JsonPropertyName("spool_weight")] public double? SpoolWeight { get; init; }
    [JsonPropertyName("article_number")] public string? ArticleNumber { get; init; }
    [JsonPropertyName("comment")] public string? Comment { get; init; }
    [JsonPropertyName("settings_extruder_temp")] public int? SettingsExtruderTemp { get; init; }
    [JsonPropertyName("settings_bed_temp")] public int? SettingsBedTemp { get; init; }
    [JsonPropertyName("color_hex")] public string? ColorHex { get; init; }
    [JsonPropertyName("multi_color_hexes")] public string? MultiColorHexes { get; init; }
    [JsonPropertyName("multi_color_direction")] public string? MultiColorDirection { get; init; }
    [JsonPropertyName("external_id")] public string? ExternalId { get; init; }
    [JsonPropertyName("extra")] public IReadOnlyDictionary<string, string> Extra { get; init; } = SpoolmanJson.NoExtra;
    [JsonPropertyName("tags")] public IReadOnlyList<SpoolmanTag> Tags { get; init; } = [];
}

/// <summary>An NFC/RFID tag. Always an empty list in phase 1; tags are #172.</summary>
[ImmutableObject(true)]
public sealed record SpoolmanTag(
    [property: JsonPropertyName("uid")] string Uid,
    [property: JsonPropertyName("added")] DateTime Added)
{
    [JsonPropertyName("format")] public string? Format { get; init; }
}

/// <summary>A Spoolman spool: one of the user's filament-category materials.</summary>
[ImmutableObject(true)]
public sealed record SpoolmanSpool(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("registered")] DateTime Registered,
    [property: JsonPropertyName("filament")] SpoolmanFilament Filament,
    [property: JsonPropertyName("used_weight")] double UsedWeight,
    [property: JsonPropertyName("used_length")] double UsedLength,
    [property: JsonPropertyName("archived")] bool Archived)
{
    [JsonPropertyName("first_used")] public DateTime? FirstUsed { get; init; }
    [JsonPropertyName("last_used")] public DateTime? LastUsed { get; init; }
    [JsonPropertyName("price")] public double? Price { get; init; }
    [JsonPropertyName("remaining_weight")] public double? RemainingWeight { get; init; }
    [JsonPropertyName("initial_weight")] public double? InitialWeight { get; init; }
    [JsonPropertyName("spool_weight")] public double? SpoolWeight { get; init; }
    [JsonPropertyName("remaining_length")] public double? RemainingLength { get; init; }
    [JsonPropertyName("location")] public string? Location { get; init; }
    [JsonPropertyName("lot_nr")] public string? LotNr { get; init; }
    [JsonPropertyName("comment")] public string? Comment { get; init; }
    [JsonPropertyName("extra")] public IReadOnlyDictionary<string, string> Extra { get; init; } = SpoolmanJson.NoExtra;
    [JsonPropertyName("tags")] public IReadOnlyList<SpoolmanTag> Tags { get; init; } = [];
}

/// <summary>
/// One user's whole Spoolman view, as cached. A wrapper rather than a bare list so it can carry
/// <see cref="ImmutableObjectAttribute"/>, which lets HybridCache hand out the stored instance
/// instead of deserializing a copy on every hit. Treat it as read-only.
/// </summary>
[ImmutableObject(true)]
public sealed record SpoolmanInventory(
    [property: JsonPropertyName("spools")] IReadOnlyList<SpoolmanSpool> Spools,
    [property: JsonPropertyName("vendors")] IReadOnlyList<SpoolmanVendor> Vendors);

public sealed record SpoolmanInfo(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("debug_mode")] bool DebugMode,
    [property: JsonPropertyName("automatic_backups")] bool AutomaticBackups,
    [property: JsonPropertyName("data_dir")] string DataDir,
    [property: JsonPropertyName("logs_dir")] string LogsDir,
    [property: JsonPropertyName("backups_dir")] string BackupsDir,
    [property: JsonPropertyName("db_type")] string DbType,
    [property: JsonPropertyName("external_db_name")] string ExternalDbName)
{
    [JsonPropertyName("git_commit")] public string? GitCommit { get; init; }
    [JsonPropertyName("build_date")] public DateTime? BuildDate { get; init; }
}

public sealed record SpoolmanHealth([property: JsonPropertyName("status")] string Status);

public sealed record SpoolmanMessage([property: JsonPropertyName("message")] string Message);

/// <summary>A setting. <see cref="Value"/> is itself JSON-encoded, as in Spoolman: <c>"\"USD\""</c>.</summary>
public sealed record SpoolmanSettingResponse(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("is_set")] bool IsSet,
    [property: JsonPropertyName("type")] string Type);

/// <summary>The body of <c>PUT /spool/{id}/use</c>. Exactly one of the two must be set.</summary>
public sealed class SpoolmanUseRequest
{
    [JsonPropertyName("use_length")] public double? UseLength { get; set; }
    [JsonPropertyName("use_weight")] public double? UseWeight { get; set; }
}

/// <summary>
/// The serializer for every <c>/spoolman</c> body. Source-generated and self-contained, so the
/// app's global JSON options (camelCase, the hot-path context, MVC's copy) cannot reach these
/// shapes. Relaxed escaping, like FastAPI's <c>ensure_ascii=False</c>, so a spool named "Grün"
/// reads the same as it would from Spoolman.
/// </summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(SpoolmanDateTimeConverter)])]
[JsonSerializable(typeof(SpoolmanSpool))]
[JsonSerializable(typeof(IReadOnlyList<SpoolmanSpool>))]
[JsonSerializable(typeof(IReadOnlyList<SpoolmanFilament>))]
[JsonSerializable(typeof(IReadOnlyList<SpoolmanVendor>))]
[JsonSerializable(typeof(SpoolmanInventory))]
[JsonSerializable(typeof(SpoolmanInfo))]
[JsonSerializable(typeof(SpoolmanHealth))]
[JsonSerializable(typeof(SpoolmanMessage))]
[JsonSerializable(typeof(SpoolmanSettingResponse))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, SpoolmanSettingResponse>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(SpoolmanUseRequest))]
public sealed partial class SpoolmanJsonContext : JsonSerializerContext
{
    /// <summary>The context with relaxed escaping; use this one, not <c>Default</c>.</summary>
    public static SpoolmanJsonContext Wire { get; } = new(new JsonSerializerOptions
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new SpoolmanDateTimeConverter() },
    });
}

public static class SpoolmanJson
{
    public static readonly IReadOnlyDictionary<string, string> NoExtra = new Dictionary<string, string>();

    /// <summary>
    /// Spoolman's datetime format: Python's <c>isoformat()</c> of a UTC datetime with
    /// <c>+00:00</c> replaced by <c>Z</c>. Whole seconds print without a fraction, anything else
    /// with exactly six digits.
    /// </summary>
    public static string FormatDateTime(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        // Microsecond precision, as Python's datetime has.
        utc = new DateTime(utc.Ticks - (utc.Ticks % 10), DateTimeKind.Utc);
        return utc.Ticks % TimeSpan.TicksPerSecond == 0
            ? utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : utc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
    }
}

/// <summary>Writes <see cref="SpoolmanJson.FormatDateTime"/>; reads any ISO 8601 value.</summary>
public sealed class SpoolmanDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTime.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(SpoolmanJson.FormatDateTime(value));
}
