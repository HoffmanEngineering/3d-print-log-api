namespace PrintLogApi.Achievements;

/// <summary>
/// One achievement family. A one-time badge has a single threshold of 1; a tiered one has up to
/// six strictly ascending thresholds, in the same display units its metric returns (hours, kg,
/// days, weeks or a count).
/// </summary>
/// <param name="Key">Permanent kebab-case key. May be retired, never renamed or deleted.</param>
/// <param name="Glyph">Glyph id; the UI maps it to SVG path data.</param>
/// <param name="DescriptionTemplate">What to do. <c>{n}</c> is the tier threshold, <c>{s}</c> pluralizes.</param>
/// <param name="UnlockedTemplate">Past-tense copy for the unlock notification, same placeholders.</param>
/// <param name="HintOrder">Above 0 for Getting started badges, in hint-card sequence.</param>
public sealed record AchievementDefinition(
    string Key,
    AchievementCategory Category,
    string Glyph,
    string Title,
    string DescriptionTemplate,
    string UnlockedTemplate,
    IReadOnlyList<long> Thresholds,
    AchievementTrigger Triggers,
    string MetricKey,
    int HintOrder = 0,
    bool Retired = false)
{
    /// <summary>Hidden badges are omitted from the public catalog until earned.</summary>
    public bool Hidden => Category == AchievementCategory.Hidden;

    public bool IsTiered => Thresholds.Count > 1;
}
