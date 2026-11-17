using System.Globalization;
using System.Text.Json;

namespace PrintLogApi.SpoolTags.Decoding;

/// <summary>
/// OpenSpool tags: an NDEF <c>application/json</c> record on a Type 2 or NFC-V tag whose JSON has
/// <c>"protocol":"openspool"</c>. Numbers may arrive as JSON numbers or numeric strings; values
/// that do not parse are ignored. The tag holds no secrets, so nothing needs redacting.
/// </summary>
public sealed class NdefOpenSpoolDecoder : ITagDecoder
{
    public const int CurrentVersion = 1;

    private const string FormatName = "openspool";
    private const string JsonMimeType = "application/json";
    private const byte TnfMediaType = 0x02;

    public string Format => FormatName;

    public int Version => CurrentVersion;

    public bool CanDecode(TagMemory memory)
    {
        return NdefReader.Find(memory) is not null;
    }

    public TagReading Decode(string uid, TagMemory memory)
    {
        // Find returns null for memory it cannot read, which includes chunked records.
        var message = NdefReader.Find(memory)
            ?? throw new TagNotRecognizedException("The tag has no readable NDEF message.");
        if (message.Records.Count == 0)
        {
            throw new BlankTagException();
        }

        foreach (var record in message.Records)
        {
            if (record.Tnf != TnfMediaType || !string.Equals(record.Type, JsonMimeType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryParse(record.Payload) is { } reading)
            {
                return reading;
            }
        }

        throw new TagNotRecognizedException("The tag holds no OpenSpool record.");
    }

    public byte[] Redact(TagMemory memory)
    {
        return (byte[])memory.Data.Clone();
    }

    private static TagReading? TryParse(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || Text(root, "protocol") is not { } protocol
                || !string.Equals(protocol, FormatName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var reading = new TagReading
            {
                Format = FormatName,
                DecoderVersion = CurrentVersion,
                MaterialType = Text(root, "type"),
                Brand = Text(root, "brand"),
                HotendMinC = Int(root, "min_temp"),
                HotendMaxC = Int(root, "max_temp"),
                BedMinC = Int(root, "bed_min_temp"),
                BedMaxC = Int(root, "bed_max_temp"),
                NetWeightG = Int(root, "weight"),
                DiameterMm = Number(root, "diameter"),
            };

            if (Text(root, "color_hex") is { } color)
            {
                color = color.TrimStart('#').ToUpperInvariant();
                if (color.Length > 0)
                {
                    reading.Colors.Add(color);
                }
            }

            return reading;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static double? Number(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        double result;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                result = value.GetDouble();
                break;
            case JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                break;
            default:
                return null;
        }

        return double.IsFinite(result) ? result : null;
    }

    private static int? Int(JsonElement root, string name)
    {
        var number = Number(root, name);
        if (number is null || Math.Abs(number.Value) > int.MaxValue)
        {
            return null;
        }

        return (int)Math.Round(number.Value);
    }
}
