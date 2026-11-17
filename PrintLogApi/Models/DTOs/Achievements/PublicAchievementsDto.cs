namespace PrintLogApi.Models.DTOs.Achievements;

/// <summary>One earned family on a public profile: earned tiers only, never progress.</summary>
public sealed record PublicFamilyDto(string Key, int HighestTier, IReadOnlyList<EarnedTierDto> Tiers);

/// <param name="Featured">Up to four families: highest tier, then rarest, then most recent.</param>
/// <param name="RevealedHidden">Catalog entries for earned hidden families; the owner revealed them by earning them.</param>
public sealed record PublicAchievementsDto(
    int EarnedTierCount,
    IReadOnlyList<PublicFamilyDto> Featured,
    IReadOnlyList<PublicFamilyDto> Families,
    IReadOnlyList<AchievementFamilyDto> RevealedHidden)
{
    /// <summary>What every refusal returns, so a public route never sees a 403 or 404.</summary>
    public static readonly PublicAchievementsDto Empty = new(0, [], [], []);
}
