using Microsoft.EntityFrameworkCore;
using PrintLogApi.Achievements.Metrics;
using PrintLogApi.Achievements.Triggers;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Achievements;

namespace PrintLogApi.Achievements;

public interface IAchievementQueryService
{
    Task<AchievementCatalogDto> GetCatalogAsync(CancellationToken ct);

    /// <summary>Runs a full reconciliation pass for the user, then describes what they hold.</summary>
    Task<MyAchievementsDto> GetMineAsync(long userId, CancellationToken ct);

    /// <exception cref="ArgumentException">The key or tier is not in the catalog.</exception>
    Task DismissHintAsync(long userId, string key, int tier, CancellationToken ct);
}

public sealed class AchievementQueryService(
    PrintLogContext db,
    IAchievementEvaluator evaluator,
    AchievementTriggerTracker tracker,
    AchievementRarityService rarity) : IAchievementQueryService
{
    public const int DismissedHintSettingTypeId = 19;

    public async Task<AchievementCatalogDto> GetCatalogAsync(CancellationToken ct)
    {
        var table = await rarity.GetAsync(ct);
        var visible = AchievementCatalog.Definitions.Where(d => !d.Retired && !d.Hidden).Select(d => ToFamily(d, table)).ToList();
        var hiddenCount = AchievementCatalog.Definitions.Count(d => !d.Retired && d.Hidden);
        return new AchievementCatalogDto(AchievementCatalog.Version, hiddenCount, visible);
    }

    public async Task<MyAchievementsDto> GetMineAsync(long userId, CancellationToken ct)
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

        var dismissed = await db.UserSettings
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
