namespace PrintLogApi.Achievements.Metrics;

/// <summary>
/// A measured value, in the same display units as the thresholds that read it. For counts
/// <c>Best == Current</c>; for streaks <c>Best</c> is the longest ever and <c>Current</c> is the
/// streak still running.
/// </summary>
public readonly record struct MetricValue(long Best, long Current)
{
    public static MetricValue Count(long n) => new(n, n);
}

/// <summary>
/// One named measurement over a user's data. Stateless and registered as a singleton; everything
/// per-pass (the user, the context, memoized queries) lives on <see cref="EvaluationContext"/>.
/// </summary>
public interface IAchievementMetric
{
    string Key { get; }

    Task<MetricValue> MeasureAsync(EvaluationContext ctx, CancellationToken ct);
}

/// <summary>A metric defined by a delegate, which is all most of them need.</summary>
public sealed class DelegateMetric(string key, Func<EvaluationContext, CancellationToken, Task<MetricValue>> measure) : IAchievementMetric
{
    public string Key { get; } = key;

    public Task<MetricValue> MeasureAsync(EvaluationContext ctx, CancellationToken ct) => measure(ctx, ct);
}
