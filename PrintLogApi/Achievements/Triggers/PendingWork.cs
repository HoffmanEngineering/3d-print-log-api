namespace PrintLogApi.Achievements.Triggers;

/// <summary>
/// The triggers one or more saves recorded, waiting to run as evaluation passes.
/// </summary>
/// <param name="ByUser">Flags per owning user.</param>
/// <param name="PrintIdTriggers">
/// Flags for child rows that only know their print (comments, filament usage). The runner maps
/// each print to its owner, so the save hook itself never queries the database.
/// </param>
public sealed record PendingWork(
    IReadOnlyDictionary<long, AchievementTrigger> ByUser,
    IReadOnlyDictionary<long, AchievementTrigger> PrintIdTriggers)
{
    public static readonly PendingWork Empty = new(new Dictionary<long, AchievementTrigger>(), new Dictionary<long, AchievementTrigger>());

    public bool IsEmpty => ByUser.Count == 0 && PrintIdTriggers.Count == 0;

    public PendingWork Merge(PendingWork other)
    {
        if (other.IsEmpty) return this;
        if (IsEmpty) return other;
        return new PendingWork(Combine(ByUser, other.ByUser), Combine(PrintIdTriggers, other.PrintIdTriggers));
    }

    private static Dictionary<long, AchievementTrigger> Combine(
        IReadOnlyDictionary<long, AchievementTrigger> a, IReadOnlyDictionary<long, AchievementTrigger> b)
    {
        var merged = new Dictionary<long, AchievementTrigger>(a);
        foreach (var (key, flags) in b)
        {
            merged[key] = merged.GetValueOrDefault(key) | flags;
        }
        return merged;
    }
}
