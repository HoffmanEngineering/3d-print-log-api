using System.Text.RegularExpressions;

namespace PrintLogApi.SpoolTags;

/// <summary>
/// Clamps whatever a decoder produced to values the rest of the system can trust. Returns a new
/// instance. On every numeric field 0 means "not set" and becomes null without a warning.
/// </summary>
public static partial class TagReadingSanitizer
{
    private const int MaxTempC = 500;
    private const int MaxDryTempC = 150;
    private const int MaxDryHours = 48;
    private const int MaxWeightG = 10000;
    private const int MaxLengthM = 10000;
    private const int MaxColors = 5;
    private const int MaxExtras = 20;
    private const int MaxExtraKey = 50;
    private const int MaxExtraValue = 200;
    private const int MaxText = 255;
    private const int MaxSku = 64;
    private const int MaxFormat = 50;
    private const int MaxWarnings = 20;
    private const int MaxWarningLength = 50;
    private const double DiameterTolerance = 0.01;

    private static readonly double[] Diameters = [1.75, 2.85, 3.0];

    [GeneratedRegex("^[0-9A-F]{6}$")]
    private static partial Regex HexColor();

    public static TagReading Sanitize(TagReading r)
    {
        var warnings = new Warnings();
        foreach (var w in r.Warnings ?? [])
        {
            warnings.Add(w);
        }

        return new TagReading
        {
            Format = Text(r.Format, MaxFormat),
            DecoderVersion = r.DecoderVersion,
            Brand = Text(r.Brand, MaxText),
            Manufacturer = Text(r.Manufacturer, MaxText),
            MaterialType = Text(r.MaterialType, MaxText),
            MaterialSubtype = Text(r.MaterialSubtype, MaxText),
            MaterialSubtypeCode = NotZero(r.MaterialSubtypeCode),
            Colors = Colors(r.Colors),
            Alpha = NotZero(r.Alpha) is >= 1 and <= 255 ? r.Alpha : null,
            DiameterMm = Diameter(r.DiameterMm, warnings),
            NetWeightG = Ranged(r.NetWeightG, MaxWeightG, "weight-out-of-range", warnings),
            LengthM = Ranged(r.LengthM, MaxLengthM, "length-out-of-range", warnings),
            HotendMinC = Ranged(r.HotendMinC, MaxTempC, "hotend-out-of-range", warnings),
            HotendMaxC = Ranged(r.HotendMaxC, MaxTempC, "hotend-out-of-range", warnings),
            FirstLayerHotendC = Ranged(r.FirstLayerHotendC, MaxTempC, "hotend-out-of-range", warnings),
            HotendC = Ranged(r.HotendC, MaxTempC, "hotend-out-of-range", warnings),
            BedMinC = Ranged(r.BedMinC, MaxTempC, "bed-out-of-range", warnings),
            BedMaxC = Ranged(r.BedMaxC, MaxTempC, "bed-out-of-range", warnings),
            DryTempC = Ranged(r.DryTempC, MaxDryTempC, "dry-out-of-range", warnings),
            DryHours = Ranged(r.DryHours, MaxDryHours, "dry-out-of-range", warnings),
            Sku = Text(r.Sku, MaxSku),
            ManufacturedOn = r.ManufacturedOn,
            Extras = Extras(r.Extras),
            Warnings = warnings.Items,
        };
    }

    private static int? NotZero(int? value)
    {
        return value is null or 0 ? null : value;
    }

    private static int? Ranged(int? value, int max, string warning, Warnings warnings)
    {
        if (value is null or 0)
        {
            return null;
        }

        if (value < 0 || value > max)
        {
            warnings.Add(warning);
            return null;
        }

        return value;
    }

    private static double? Diameter(double? value, Warnings warnings)
    {
        if (value is null || value == 0)
        {
            return null;
        }

        foreach (var d in Diameters)
        {
            if (Math.Abs(value.Value - d) <= DiameterTolerance)
            {
                return d;
            }
        }

        warnings.Add("diameter-unsupported");
        return null;
    }

    private static string? Text(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }

    private static List<string> Colors(List<string>? colors)
    {
        var result = new List<string>();
        foreach (var raw in colors ?? [])
        {
            if (result.Count == MaxColors)
            {
                break;
            }

            var color = raw?.Trim().TrimStart('#').ToUpperInvariant();
            if (color is not null && HexColor().IsMatch(color))
            {
                result.Add(color);
            }
        }

        return result;
    }

    private static Dictionary<string, string> Extras(Dictionary<string, string>? extras)
    {
        var result = new Dictionary<string, string>();
        foreach (var (key, value) in extras ?? [])
        {
            if (result.Count == MaxExtras)
            {
                break;
            }

            if (key is { Length: > 0 and <= MaxExtraKey } && value is not null && value.Length <= MaxExtraValue)
            {
                result[key] = value;
            }
        }

        return result;
    }

    /// <summary>At most <see cref="MaxWarnings"/> distinct warnings, each cut to <see cref="MaxWarningLength"/> characters.</summary>
    private sealed class Warnings
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public List<string> Items { get; } = [];

        public void Add(string? warning)
        {
            if (Items.Count >= MaxWarnings || string.IsNullOrWhiteSpace(warning))
            {
                return;
            }

            var text = warning.Length > MaxWarningLength ? warning[..MaxWarningLength] : warning;
            if (_seen.Add(text))
            {
                Items.Add(text);
            }
        }
    }
}
