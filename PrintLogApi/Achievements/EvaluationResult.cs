using PrintLogApi.Achievements.Metrics;
using PrintLogApi.Models;

namespace PrintLogApi.Achievements;

/// <param name="Granted">Tiers this pass granted (and committed).</param>
/// <param name="Metrics">Every metric the pass measured; all of them in <see cref="EvaluationMode.Full"/>.</param>
/// <param name="Held">Every tier the user holds after the pass.</param>
public sealed record EvaluationResult(
    IReadOnlyList<(string Key, int Tier)> Granted,
    IReadOnlyDictionary<string, MetricValue> Metrics,
    IReadOnlyList<UserAchievement> Held)
{
    public static readonly EvaluationResult Empty = new([], new Dictionary<string, MetricValue>(), []);
}
