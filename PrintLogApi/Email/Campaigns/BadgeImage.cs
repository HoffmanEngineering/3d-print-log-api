using PrintLogApi.Achievements;

namespace PrintLogApi.Email.Campaigns;

/// <summary>
/// The file name of a badge's email image. The UI's <c>scripts/generate-email-assets.mjs</c>
/// renders the same names from the same rule (its <c>badgeFillId</c>), so keep the two in step:
/// neither repo's CI can see the other side, and <c>EmailAssetSmokeTests</c> checks production.
/// </summary>
public static class BadgeImage
{
    public static string FileName(AchievementDefinition def, int tier)
    {
        var glyph = def.Glyph == "numeral"
            ? $"numeral-{def.Thresholds[tier - 1]}"
            : def.Glyph.Replace("initials:", "initials-", StringComparison.Ordinal).ToLowerInvariant();
        return $"{glyph}-{Fill(def, tier)}.png";
    }

    // One-time badges and hidden ones wear their category's color; tiered ones the tier's finish.
    // Milestones and Streaks have no category color, so a one-time one falls back to Getting
    // started's, exactly as in the app.
    private static string Fill(AchievementDefinition def, int tier)
    {
        if (def.Thresholds.Count == 1 || def.Category == AchievementCategory.Hidden)
        {
            return def.Category switch
            {
                AchievementCategory.Integrations => "in",
                AchievementCategory.Community => "co",
                AchievementCategory.Hidden => "hi",
                _ => "gs",
            };
        }

        return $"t{Math.Clamp(tier, 1, 6)}";
    }
}
