using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Achievements.Metrics;
using PrintLogApi.Models;
using PrintLogApi.Services;

namespace PrintLogApi.Achievements;

/// <summary>
/// One evaluation pass for one user, over the scoped context it was resolved with. Callers that
/// run it from a save hook must suppress that context first, so the pass's own save raises no
/// further triggers (see <c>AchievementTriggerTracker</c>).
/// </summary>
public sealed class AchievementEvaluator(
    PrintLogContext db,
    IEnumerable<IAchievementMetric> metrics,
    INotificationService notifications,
    ICatalogVersionProvider catalogVersion,
    TimeProvider clock,
    ILogger<AchievementEvaluator> logger) : IAchievementEvaluator
{
    private readonly IReadOnlyList<IAchievementMetric> _metrics = metrics.ToList();

    /// <summary>
    /// Test seam: runs after held tiers are loaded and before the grants are saved, which is
    /// exactly the window a concurrent pass races into.
    /// </summary>
    internal Func<Task>? BeforePersist { get; set; }

    public async Task<EvaluationResult> EvaluateAsync(long userId, AchievementTrigger triggers, EvaluationMode mode, CancellationToken ct = default)
    {
        // Tracked on purpose: a catch-up advances the version in the same save as the grants.
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return EvaluationResult.Empty;
        }

        var targetVersion = catalogVersion.Version;
        var catchUp = user.AchievementCatalogVersion < targetVersion;
        // Only the launch catch-up (from never evaluated) is retroactive. A later version bump
        // grants normally and celebrates.
        var retroactive = catchUp && user.AchievementCatalogVersion == 0;

        var selected = AchievementCatalog.Definitions
            .Where(d => !d.Retired && (catchUp || mode == EvaluationMode.Full || (d.Triggers & triggers) != 0))
            .ToList();
        if (selected.Count == 0)
        {
            return EvaluationResult.Empty;
        }

        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var ctx = await EvaluationContext.CreateAsync(db, userId, nowUtc, _metrics, ct);
        foreach (var metricKey in selected.Select(d => d.MetricKey).Distinct())
        {
            await ctx.MeasureAsync(metricKey, ct);
        }

        var held = await LoadHeldAsync(userId, ct);
        var missing = Missing(selected, ctx.Measured, held);
        if (missing.Count == 0 && !catchUp)
        {
            return new EvaluationResult([], ctx.Measured, held);
        }

        if (BeforePersist is { } hook)
        {
            await hook();
        }

        try
        {
            await PersistAsync(user, missing, held, retroactive, catchUp ? targetVersion : null, nowUtc, ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A concurrent pass committed at least one of these tiers first, and the database
            // rejected the whole batch. Start over from what is held now, once.
            DetachAdded();
            held = await LoadHeldAsync(userId, ct);
            missing = Missing(selected, ctx.Measured, held);
            try
            {
                await PersistAsync(user, missing, held, retroactive, catchUp ? targetVersion : null, nowUtc, ct);
            }
            catch (DbUpdateException retryEx) when (IsUniqueViolation(retryEx))
            {
                DetachAdded();
                logger.LogWarning(retryEx, "Achievement grants for user {UserId} conflicted twice; leaving them to reconciliation", userId);
                return new EvaluationResult([], ctx.Measured, await LoadHeldAsync(userId, ct));
            }
        }

        var granted = missing.Select(m => (m.Def.Key, m.Tier)).ToList();
        return new EvaluationResult(granted, ctx.Measured, await LoadHeldAsync(userId, ct));
    }

    private Task<List<UserAchievement>> LoadHeldAsync(long userId, CancellationToken ct) =>
        db.UserAchievements.AsNoTracking().Where(a => a.UserId == userId).ToListAsync(ct);

    private static List<(AchievementDefinition Def, int Tier)> Missing(
        IEnumerable<AchievementDefinition> selected, IReadOnlyDictionary<string, MetricValue> measured, IReadOnlyCollection<UserAchievement> held)
    {
        var heldSet = held.Select(h => (h.AchievementKey, h.Tier)).ToHashSet();
        var missing = new List<(AchievementDefinition, int)>();
        foreach (var def in selected)
        {
            var best = measured[def.MetricKey].Best;
            for (var tier = 1; tier <= def.Thresholds.Count; tier++)
            {
                if (best >= def.Thresholds[tier - 1] && !heldSet.Contains((def.Key, tier)))
                {
                    missing.Add((def, tier));
                }
            }
        }
        return missing;
    }

    /// <summary>
    /// Adds the grants, their notifications and the catalog version, and commits them in one save
    /// (the notification service's save; see <see cref="INotificationService.PersistAchievementNotifications"/>).
    /// </summary>
    private async Task PersistAsync(
        User user, IReadOnlyList<(AchievementDefinition Def, int Tier)> missing, IReadOnlyList<UserAchievement> held,
        bool retroactive, int? newVersion, DateTime nowUtc, CancellationToken ct)
    {
        db.UserAchievements.AddRange(missing.Select(m => new UserAchievement
        {
            UserId = user.Id,
            AchievementKey = m.Def.Key,
            Tier = m.Tier,
            UnlockedAt = nowUtc,
            Retroactive = retroactive,
        }));

        var notes = missing.Select(m => AchievementNotificationFactory.ForGrant(user.Id, m.Def, m.Tier, retroactive)).ToList();
        if (retroactive && missing.Count > 0)
        {
            // Everything the user will hold that predates achievements, the migration's
            // plugged-in row included.
            notes.Add(AchievementNotificationFactory.Summary(user.Id, held.Count(h => h.Retroactive) + missing.Count));
        }

        if (newVersion is { } version)
        {
            user.AchievementCatalogVersion = version;
        }

        if (notes.Count > 0)
        {
            await notifications.PersistAchievementNotifications(notes, ct);
        }
        else
        {
            await db.SaveChangesAsync(ct);
        }
    }

    private void DetachAdded()
    {
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) => ex.InnerException switch
    {
        SqlException sql => sql.Number is 2601 or 2627,
        // SQLITE_CONSTRAINT_UNIQUE; the test database's equivalent.
        SqliteException sqlite => sqlite.SqliteExtendedErrorCode == 2067,
        _ => false,
    };
}
