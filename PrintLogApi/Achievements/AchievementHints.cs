using PrintLogApi.Achievements.Metrics;
using PrintLogApi.Models.DTOs.Achievements;

namespace PrintLogApi.Achievements;

/// <summary>Chooses the single next badge the hint card suggests (spec §5.1).</summary>
public static class AchievementHints
{
    public const string DefaultCtaRoute = "/achievements";

    /// <summary>Where the hint's button goes. Every route here exists in the web app's router.</summary>
    public static readonly IReadOnlyDictionary<string, string> CtaRoutes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["first-printer"] = "/printers/new",
        ["first-material"] = "/materials/new",
        // The create form is :id/edit; /prints/:id is the read-only view.
        ["first-print"] = "/prints/new/edit",
        ["plugged-in"] = "/docs/slic3r-uploader",
        ["first-photo"] = "/prints",
        ["maker-profile"] = "/profile",
    };

    /// <summary>
    /// The lowest-HintOrder Getting started badge not yet held; otherwise the unearned tier
    /// closest to done by <c>best / nextThreshold</c>, skipping hidden families and streaks that
    /// are not currently running. Null when that is the hint the user dismissed.
    /// </summary>
    public static NextHintDto? Choose(
        IReadOnlySet<(string Key, int Tier)> held,
        IReadOnlyDictionary<string, AchievementProgressDto?> progress,
        IReadOnlyDictionary<string, MetricValue> metrics,
        string? dismissed)
    {
        var active = AchievementCatalog.Definitions.Where(d => !d.Retired).ToList();

        (string Key, int Tier)? pick = active
            .Where(d => d.Category == AchievementCategory.GettingStarted && !held.Contains((d.Key, 1)))
            .OrderBy(d => d.HintOrder)
            .Select(d => ((string, int)?)(d.Key, 1))
            .FirstOrDefault();

        if (pick is null)
        {
            var best = active
                .Where(d => !d.Hidden && progress.GetValueOrDefault(d.Key) is not null)
                // busy-day is a max, so its Current equals its Best and only lapses at zero.
                .Where(d => d.Category != AchievementCategory.Streaks || metrics.GetValueOrDefault(d.MetricKey).Current > 0)
                .Select(d => (Def: d, Progress: progress[d.Key]!))
                .Where(x => x.Progress.Best > 0)
                .OrderByDescending(x => (double)x.Progress.Best / x.Progress.NextThreshold)
                .FirstOrDefault();
            if (best.Def is { } def)
            {
                var nextTier = Enumerable.Range(1, def.Thresholds.Count).First(t => !held.Contains((def.Key, t)));
                pick = (def.Key, nextTier);
            }
        }

        if (pick is not { } chosen || dismissed == $"{chosen.Key}:{chosen.Tier}")
        {
            return null;
        }

        return new NextHintDto(chosen.Key, chosen.Tier, CtaRoutes.GetValueOrDefault(chosen.Key, DefaultCtaRoute));
    }
}
