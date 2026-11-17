using PrintLogApi.Achievements;

namespace PrintLogApi.Models.DTOs.Achievements;

// Response shapes for AchievementsController. Positional records: every one is built
// server-side, so the constructor is the enforcement (see AGENTS.md, DTOs).

/// <param name="Description">The tier's description with its threshold filled in.</param>
/// <param name="RarityPercent">Share of evaluated makers holding this tier, 0–100.</param>
public sealed record AchievementTierDto(int Tier, long Threshold, string Description, double? RarityPercent);

public sealed record AchievementFamilyDto(
    string Key, AchievementCategory Category, string Glyph, string Title, int HintOrder, IReadOnlyList<AchievementTierDto> Tiers);

/// <param name="HiddenCount">How many hidden families exist. Their keys and copy are never sent.</param>
public sealed record AchievementCatalogDto(int Version, int HiddenCount, IReadOnlyList<AchievementFamilyDto> Families);

public sealed record EarnedTierDto(int Tier, DateTime UnlockedAt, bool Retroactive);

/// <param name="Best">Never below the threshold of the highest tier held, even after prints are deleted.</param>
public sealed record AchievementProgressDto(long Best, long Current, long NextThreshold);

/// <param name="Progress">Null when every tier is held.</param>
public sealed record MyAchievementFamilyDto(string Key, IReadOnlyList<EarnedTierDto> Tiers, AchievementProgressDto? Progress);

public sealed record NextHintDto(string Key, int Tier, string CtaRoute);

/// <param name="RevealedHidden">Full catalog entries for the hidden families this user has earned.</param>
public sealed record MyAchievementsDto(
    int EarnedTierCount,
    int TotalTierCount,
    IReadOnlyList<MyAchievementFamilyDto> Families,
    IReadOnlyList<AchievementFamilyDto> RevealedHidden,
    NextHintDto? NextHint);

/// <summary>Request body for dismissing the hint card.</summary>
public class DismissHintRequestDto
{
    public string? Key { get; set; }

    public int Tier { get; set; }
}
