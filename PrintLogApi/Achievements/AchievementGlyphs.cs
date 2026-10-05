namespace PrintLogApi.Achievements;

/// <summary>
/// The glyph ids the UI can draw. The UI keeps the same list (its badge glyph map), so a glyph
/// added here without art there would render blank.
/// </summary>
public static class AchievementGlyphs
{
    /// <summary>Glyphs drawn as two-letter initials, e.g. <c>initials:Cu</c>.</summary>
    public const string InitialsPrefix = "initials:";

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        "printer-bedslinger", "spool", "layers", "plug", "camera", "id-badge",
        "numeral", "clock", "scale", "timer", "factory", "shelf", "flask", "palette", "folder-star",
        "flame", "calendar", "stack-plates",
        "fork", "octopus", "moon-link", "robot",
        "globe", "chat", "wrench",
        "noodles", "wave", "moon", "confetti",
        "question", "stack",
    };

    public static bool IsKnown(string glyph) =>
        Known.Contains(glyph)
        || (glyph.StartsWith(InitialsPrefix, StringComparison.Ordinal) && glyph.Length == InitialsPrefix.Length + 2);
}
