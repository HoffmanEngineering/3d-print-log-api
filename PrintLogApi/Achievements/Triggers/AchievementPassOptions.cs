namespace PrintLogApi.Achievements.Triggers;

/// <summary>
/// Limits on the evaluation pass that runs after a save, on the request that made it.
/// </summary>
public sealed class AchievementPassOptions
{
    /// <summary>
    /// How long a post-save pass may hold its request. Passes take tens of milliseconds; this
    /// bounds the pathological cases (a stalled query, a connection pool drained by concurrent
    /// commits that each hold a connection while their pass waits for another). A pass canceled
    /// here loses nothing permanently: <c>/me</c> reconciliation grants whatever it would have.
    /// </summary>
    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(5);
}
