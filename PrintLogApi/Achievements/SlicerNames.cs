namespace PrintLogApi.Achievements;

/// <summary>
/// Normalizes the slicer name a plugin posts with its settings (<c>CuraSetting.Slicer</c>) into the
/// key stored on <c>Print.Slicer</c>. Keyed on the actual wire values: each Slic3r uploader
/// parser's <c>SlicerName</c>. The Cura plugin sends no name at all.
/// </summary>
public static class SlicerNames
{
    public const string Cura = "cura";
    public const string PrusaSlicer = "prusaslicer";
    public const string OrcaSlicer = "orcaslicer";
    public const string BambuStudio = "bambustudio";
    public const string Anycubic = "anycubic";
    public const string FLSun = "flsun";
    public const string Other = "other";

    // Snapmaker Orca is deliberately absent: its G-code goes through the uploader's OrcaParser
    // and posts "OrcaSlicer", so it cannot be told apart from OrcaSlicer here.
    private static readonly Dictionary<string, string> WireValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PrusaSlicer"] = PrusaSlicer,
        ["OrcaSlicer"] = OrcaSlicer,
        ["BambuStudioSlicer"] = BambuStudio,
        ["AnycubicSlicerNext"] = Anycubic,
        ["FLSunSlicer"] = FLSun,
    };

    /// <summary>The slicers that have a dedicated badge. FLSun and "other" have none.</summary>
    public static readonly IReadOnlySet<string> BadgeSlicers =
        new HashSet<string> { Cura, PrusaSlicer, OrcaSlicer, BambuStudio, Anycubic };

    /// <summary>
    /// Maps a wire value to its key. Null or empty means the Cura plugin; anything unrecognized is
    /// <see cref="Other"/>. Already-normalized keys map to themselves.
    /// </summary>
    public static string Normalize(string? wireValue)
    {
        if (string.IsNullOrWhiteSpace(wireValue))
        {
            return Cura;
        }

        var trimmed = wireValue.Trim();
        if (WireValues.TryGetValue(trimmed, out var key))
        {
            return key;
        }

        return trimmed.ToLowerInvariant() switch
        {
            Cura or PrusaSlicer or OrcaSlicer or BambuStudio or Anycubic or FLSun => trimmed.ToLowerInvariant(),
            _ => Other,
        };
    }
}
