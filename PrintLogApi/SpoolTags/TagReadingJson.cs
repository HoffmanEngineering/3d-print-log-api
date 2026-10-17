using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrintLogApi.SpoolTags;

/// <summary>The stored JSON form of a <see cref="TagReading"/>: web defaults, nulls omitted, capped at <see cref="MaxBytes"/>.</summary>
public static class TagReadingJson
{
    public const int MaxBytes = 8192;

    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Null when the UTF-8 length exceeds <see cref="MaxBytes"/>.</summary>
    public static string? Serialize(TagReading r)
    {
        var json = JsonSerializer.Serialize(r, Options);
        return Encoding.UTF8.GetByteCount(json) > MaxBytes ? null : json;
    }

    public static TagReading? Deserialize(string? json)
    {
        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<TagReading>(json, Options);
    }
}
