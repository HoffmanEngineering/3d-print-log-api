namespace PrintLogApi.Achievements;

public enum EvaluationMode
{
    /// <summary>Only definitions whose triggers overlap the recorded ones (unless catching up).</summary>
    Triggered,

    /// <summary>Every definition, measuring every metric. Used by reconciliation.</summary>
    Full,
}

/// <summary>
/// Measures a user's metrics and grants any tier they qualify for but don't hold. Grants and
/// their notifications commit together, so a notification exists exactly when its grant does.
/// </summary>
public interface IAchievementEvaluator
{
    Task<EvaluationResult> EvaluateAsync(long userId, AchievementTrigger triggers, EvaluationMode mode, CancellationToken ct = default);
}

/// <summary>The catalog version a user is caught up to. Injectable so a version bump can be tested.</summary>
public interface ICatalogVersionProvider
{
    int Version { get; }
}

public sealed class CatalogVersionProvider : ICatalogVersionProvider
{
    public int Version => AchievementCatalog.Version;
}
