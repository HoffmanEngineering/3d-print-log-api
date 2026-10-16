using System.Globalization;

namespace PrintLogApi.SpoolTags.Matching;

public static class ColorDistance
{
    /// <summary>The bridge's <c>COLOR_TOLERANCE</c>: farther than this is a different color.</summary>
    public const double Tolerance = 100;

    /// <summary>Euclidean RGB distance (0-441), or null when either color is missing or not six hex digits.</summary>
    public static double? Rgb(string? a, string? b)
    {
        if (!TryParse(a, out var x) || !TryParse(b, out var y))
        {
            return null;
        }
        var dr = x.R - y.R;
        var dg = x.G - y.G;
        var db = x.B - y.B;
        return Math.Sqrt((dr * dr) + (dg * dg) + (db * db));
    }

    /// <summary>1 at distance 0, falling to 0 at the bridge's tolerance of 100. 0 when there is no distance.</summary>
    public static double Closeness(double? distance) =>
        distance is { } d ? Math.Max(0, 1 - (d / Tolerance)) : 0;

    private static bool TryParse(string? hex, out (int R, int G, int B) rgb)
    {
        rgb = default;
        var s = hex?.Trim().TrimStart('#');
        if (s is not { Length: 6 }
            || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
        {
            return false;
        }
        rgb = ((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
        return true;
    }
}
