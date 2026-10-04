using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using PrintLogApi.Caching;

namespace PrintLogApi.Achievements;

public sealed record RarityEntry(string Key, int Tier, double Percent);

/// <summary>
/// Holder share per tier. A list rather than a dictionary because HybridCache serializes what it
/// stores, and a tuple-keyed dictionary does not serialize. Read-only once built.
/// </summary>
[ImmutableObject(true)]
public sealed class RarityTable
{
    private Dictionary<(string, int), double>? _lookup;

    public List<RarityEntry> Entries { get; init; } = [];

    /// <summary>Percent of evaluated makers holding the tier; 0 when nobody does.</summary>
    public double PercentFor(string key, int tier)
    {
        _lookup ??= Entries.ToDictionary(e => (e.Key, e.Tier), e => e.Percent);
        return _lookup.GetValueOrDefault((key, tier));
    }
}

/// <summary>
/// "Earned by 9% of makers". Computed by one grouped query and cached for an hour through
/// HybridCache (see AGENTS.md, Caching): rarity drifts slowly, and a stale hour costs nothing.
/// </summary>
public sealed class AchievementRarityService(HybridCache cache, CachedComputation computation)
{
    public const string CacheKey = "achievements:rarity:v1";

    private static readonly HybridCacheEntryOptions Options = new()
    {
        Expiration = TimeSpan.FromHours(1),
        LocalCacheExpiration = TimeSpan.FromHours(1),
    };

    public async Task<RarityTable> GetAsync(CancellationToken ct) =>
        await cache.GetOrCreateAsync(
            CacheKey,
            computation,
            static (state, token) => state.RunAsync(
                (services, t) => ComputeAsync(services.GetRequiredService<PrintLogContext>(), t), token),
            Options,
            cancellationToken: ct);

    /// <summary>
    /// Holders over users evaluated at least once (<c>AchievementCatalogVersion &gt; 0</c>), on both
    /// sides of the fraction: the launch backfill grants rows to users who never come back, and
    /// counting those would let a tier exceed 100%.
    /// </summary>
    public static async Task<RarityTable> ComputeAsync(PrintLogContext db, CancellationToken ct)
    {
        // One statement, so the holder counts and the evaluated-user count come from the same
        // read: two separate queries could see a user finish catch-up in between and report
        // over 100%. The clamp is only a guard on top of that.
        var holders = await db.UserAchievements
            .AsNoTracking()
            .Where(a => a.User.AchievementCatalogVersion > 0)
            .GroupBy(a => new { a.AchievementKey, a.Tier })
            .Select(g => new
            {
                g.Key.AchievementKey,
                g.Key.Tier,
                Count = g.Count(),
                Evaluated = db.Users.Count(u => u.AchievementCatalogVersion > 0),
            })
            .ToListAsync(ct);

        return new RarityTable
        {
            Entries = holders
                .Where(h => h.Evaluated > 0)
                .Select(h => new RarityEntry(h.AchievementKey, h.Tier, Math.Min(100.0, 100.0 * h.Count / h.Evaluated)))
                .ToList(),
        };
    }
}
