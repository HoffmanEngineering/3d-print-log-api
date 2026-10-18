using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PrintLogApi.PrinterCatalog.Generation;

/// <summary>
/// Reading literal values out of slicer profile JSON, where a number may be a JSON number or a
/// string ("0.4") and a boolean may be <c>true</c>, <c>"1"</c> or <c>"True"</c>.
/// </summary>
internal static class ProfileValues
{
    internal static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        AllowDuplicateProperties = false,
    };

    /// <summary>
    /// The file's top-level object, or null (logged) when it can't be read, isn't JSON, isn't an
    /// object or repeats a property. One bad profile skips that file, never the whole run.
    /// </summary>
    internal static JsonObject? ReadObject(string path, Action<string>? log)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path), documentOptions: DocumentOptions) is not JsonObject obj)
            {
                log?.Invoke($"skipping {path}: not a JSON object");
                return null;
            }
            // JsonNode builds its children lazily, so a problem deep in the file would otherwise
            // surface later, outside this try. Walk it all now.
            Materialize(obj);
            return obj;
        }
        catch (Exception e) when (e is JsonException or IOException or ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            log?.Invoke($"skipping {path}: {e.Message}");
            return null;
        }
    }

    private static void Materialize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, child) in obj)
                {
                    Materialize(child);
                }
                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    Materialize(child);
                }
                break;
        }
    }

    internal static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static double? AsDouble(JsonNode? node)
    {
        if (node is not JsonValue v)
        {
            return null;
        }
        if (v.GetValueKind() == JsonValueKind.Number)
        {
            return v.GetValue<double>();
        }
        return v.TryGetValue<string>(out var s) ? ParseDouble(s) : null;
    }

    internal static double? ParseDouble(string? s) =>
        double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;

    internal static int? AsInt(JsonNode? node) =>
        AsDouble(node) is { } d && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue ? (int)d : null;

    internal static bool? AsBool(JsonNode? node)
    {
        if (node is not JsonValue v)
        {
            return null;
        }
        switch (v.GetValueKind())
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                return v.GetValue<double>() != 0;
            case JsonValueKind.String:
                var s = v.GetValue<string>().Trim();
                if (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1")
                {
                    return true;
                }
                if (s.Equals("false", StringComparison.OrdinalIgnoreCase) || s == "0")
                {
                    return false;
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>True for a JSON number or boolean, or a string holding one ("0.4", "True").</summary>
    internal static bool IsLiteral(JsonNode? node)
    {
        if (node is not JsonValue v)
        {
            return false;
        }
        return v.GetValueKind() switch
        {
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => true,
            JsonValueKind.String => v.GetValue<string>().Trim() is var s
                && (ParseDouble(s) is not null
                    || s.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("false", StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
    }
}
