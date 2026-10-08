using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Achievements.Metrics;
using PrintLogApi.Achievements.Triggers;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Achievements;

namespace PrintLogApi.Achievements;

public interface IAchievementQueryService
{
    Task<AchievementCatalogDto> GetCatalogAsync(CancellationToken ct);

    /// <summary>Runs a full reconciliation pass for the user, then describes what they hold.</summary>
    /// <param name="honorDismissedHint">
    /// True on the in-app hint card, where dismissing means "stop showing me this here". Email passes
    /// false: a dismissal is not a request to drop the suggestion everywhere, and a null hint there
    /// has nothing better to fall back to.
    /// </param>
    Task<MyAchievementsDto> GetMineAsync(long userId, bool honorDismissedHint, CancellationToken ct);

    /// <exception cref="ArgumentException">The key or tier is not in the catalog.</exception>
    Task DismissHintAsync(long userId, string key, int tier, CancellationToken ct);

    /// <summary>
    /// What <paramref name="viewer"/> may see of another maker's achievements. Read-only: no
    /// evaluation runs on this anonymous-safe path. Any refusal (unknown user, a profile the viewer
    /// may not see, achievements hidden from the profile) is <see cref="PublicAchievementsDto.Empty"/>.
    /// </summary>
    Task<PublicAchievementsDto> GetPublicAsync(ClaimsPrincipal viewer, long targetUserId, CancellationToken ct);
}

public sealed class AchievementQueryService(
    PrintLogContext db,
    IAchievementEvaluator evaluator,
    AchievementTriggerTracker tracker,
    AchievementRarityService rarity,
    IAuthorizationService authorization) : IAchievementQueryService
{
    public const int DismissedHintSettingTypeId = 19;
    public const int ShowOnProfileSettingTypeId = 17;
    private const int FeaturedCount = 4;

    public async Task<PublicAchievementsDto> GetPublicAsync(ClaimsPrincipal viewer, long targetUserId, CancellationToken ct)
    {
        var target = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == targetUserId, ct);
        if (target is null)
        {
            return PublicAchievementsDto.Empty;
        }

        // The same policy as the profile itself: Public and Unlisted for anyone, Friends and
        // Private for the owner only. Checking Private alone would leak Friends-only profiles.
        if (!(await authorization.AuthorizeAsync(viewer, target, "ViewUserProfile")).Succeeded)
        {
            return PublicAchievementsDto.Empty;
        }

        var isOwner = viewer.GetUserId() == targetUserId;
        if (!isOwner)
        {
            var show = await db.UserSettings
                .AsNoTracking()
                .Where(s => s.UserId == targetUserId && s.UserSettingTypeId == ShowOnProfileSettingTypeId)
                .Select(s => s.Value)
                .FirstOrDefaultAsync(ct);
            if (string.Equals(show, "false", StringComparison.OrdinalIgnoreCase))
            {
                return PublicAchievementsDto.Empty;
            }
        }

        var held = await db.UserAchievements.AsNoTracking().Where(a => a.UserId == targetUserId).ToListAsync(ct);
        var table = await rarity.GetAsync(ct);
        var families = held
            .Where(h => AchievementCatalog.Find(h.AchievementKey) is { Retired: false })
            .GroupBy(h => h.AchievementKey)
            .Select(g => new
            {
                Def = AchievementCatalog.Find(g.Key)!,
                Tiers = g.OrderBy(h => h.Tier).ToList(),
            })
            .Select(f => new
            {
                f.Def,
                Dto = new PublicFamilyDto(
                    f.Def.Key,
                    f.Tiers[^1].Tier,
                    f.Tiers.Select(h => new EarnedTierDto(h.Tier, DateTime.SpecifyKind(h.UnlockedAt, DateTimeKind.Utc), h.Retroactive)).ToList()),
                LatestUnlock = f.Tiers.Max(h => h.UnlockedAt),
            })
            .ToList();

        var featured = families
            .OrderByDescending(f => f.Dto.HighestTier)
            .ThenBy(f => table.PercentFor(f.Def.Key, f.Dto.HighestTier))
            .ThenByDescending(f => f.LatestUnlock)
            .Take(FeaturedCount)
            .Select(f => f.Dto)
            .ToList();

        var catalogOrder = AchievementCatalog.Definitions.Select((d, i) => (d.Key, i)).ToDictionary(x => x.Key, x => x.i);
        return new PublicAchievementsDto(
            families.Sum(f => f.Dto.Tiers.Count),
            featured,
            families.OrderBy(f => catalogOrder[f.Def.Key]).Select(f => f.Dto).ToList(),
            families.Where(f => f.Def.Hidden).Select(f => ToFamily(f.Def, table)).ToList());
    }

    public async Task<AchievementCatalogDto> GetCatalogAsync(CancellationToken ct)
    {
        var table = await rarity.GetAsync(ct);
        var visible = AchievementCatalog.Definitions.Where(d => !d.Retired && !d.Hidden).Select(d => ToFamily(d, table)).ToList();
        var hiddenCount = AchievementCatalog.Definitions.Count(d => !d.Retired && d.Hidden);
        return new AchievementCatalogDto(AchievementCatalog.Version, hiddenCount, visible);
    }

    public async Task<MyAchievementsDto> GetMineAsync(long userId, bool honorDismissedHint, CancellationToken ct)
    {
        // The pass saves its grants on this request's context, so mark it first: reconciling
        // must never raise triggers for yet another pass.
        tracker.Suppress(db);
        var result = await evaluator.EvaluateAsync(userId, AchievementTrigger.None, EvaluationMode.Full, ct);

        var heldByKey = result.Held
            .GroupBy(h => h.AchievementKey)
            .ToDictionary(g => g.Key, g => g.OrderBy(h => h.Tier).ToList());
        var heldSet = result.Held.Select(h => (h.AchievementKey, h.Tier)).ToHashSet();

        var families = new List<MyAchievementFamilyDto>();
        var progressByKey = new Dictionary<string, AchievementProgressDto?>(StringComparer.Ordinal);
        var revealed = new List<AchievementFamilyDto>();
        var earnedTiers = 0;
        RarityTable? table = null;

        foreach (var def in AchievementCatalog.Definitions.Where(d => !d.Retired))
        {
            var held = heldByKey.GetValueOrDefault(def.Key) ?? [];
            if (def.Hidden && held.Count == 0)
            {
                continue; // never reveal an unearned hidden family, not even its key
            }

            var progress = Progress(def, held, result.Metrics.GetValueOrDefault(def.MetricKey));
            progressByKey[def.Key] = progress;
            earnedTiers += held.Count;
            families.Add(new MyAchievementFamilyDto(
                def.Key,
                held.Select(h => new EarnedTierDto(h.Tier, DateTime.SpecifyKind(h.UnlockedAt, DateTimeKind.Utc), h.Retroactive)).ToList(),
                progress));

            if (def.Hidden)
            {
                table ??= await rarity.GetAsync(ct);
                revealed.Add(ToFamily(def, table));
            }
        }

        var dismissed = !honorDismissedHint ? null : await db.UserSettings
            .AsNoTracking()
            .Where(s => s.UserId == userId && s.UserSettingTypeId == DismissedHintSettingTypeId)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        var hint = AchievementHints.Choose(heldSet, progressByKey, result.Metrics, dismissed);

        return new MyAchievementsDto(earnedTiers, AchievementCatalog.TotalTierCount, families, revealed, hint);
    }

    public async Task DismissHintAsync(long userId, string key, int tier, CancellationToken ct)
    {
        if (AchievementCatalog.Find(key) is not { Retired: false } def || tier < 1 || tier > def.Thresholds.Count)
        {
            throw new ArgumentException($"'{key}:{tier}' is not an achievement tier.");
        }

        var value = $"{key}:{tier}";
        var setting = await db.UserSettings
            .FirstOrDefaultAsync(s => s.UserId == userId && s.UserSettingTypeId == DismissedHintSettingTypeId, ct);
        if (setting is null)
        {
            db.UserSettings.Add(new UserSetting
            {
                UserId = userId,
                UserSettingTypeId = DismissedHintSettingTypeId,
                Value = value,
                CreatedById = userId,
                UpdatedById = userId,
            });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedById = userId;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Null when every tier is held. Otherwise the next threshold, and a best that never falls
    /// below the highest threshold already earned: prints can be deleted, so the recomputed history
    /// can shrink below what the user has (spec §3.6).
    /// </summary>
    private static AchievementProgressDto? Progress(AchievementDefinition def, IReadOnlyList<UserAchievement> held, MetricValue metric)
    {
        var heldTiers = held.Select(h => h.Tier).ToHashSet();
        var next = Enumerable.Range(1, def.Thresholds.Count).FirstOrDefault(t => !heldTiers.Contains(t));
        if (next == 0)
        {
            return null;
        }

        var earnedFloor = heldTiers.Where(t => t <= def.Thresholds.Count).Select(t => def.Thresholds[t - 1]).DefaultIfEmpty(0).Max();
        return new AchievementProgressDto(Math.Max(metric.Best, earnedFloor), metric.Current, def.Thresholds[next - 1]);
    }

    private static AchievementFamilyDto ToFamily(AchievementDefinition def, RarityTable table) => new(
        def.Key,
        def.Category,
        def.Glyph,
        def.Title,
        def.HintOrder,
        def.Thresholds
            .Select((threshold, i) => new AchievementTierDto(
                i + 1, threshold, AchievementCopy.Format(def.DescriptionTemplate, threshold), table.PercentFor(def.Key, i + 1)))
            .ToList());
}
