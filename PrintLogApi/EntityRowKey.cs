using System.Collections;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace PrintLogApi;

/// <summary>
/// Identifies the database row a tracked entry stands for: the root of its entity type's
/// hierarchy (so a TPH subtype matches its base) and its primary key values, compared value by
/// value. Comparing values rather than a joined string keeps composite keys with separator
/// characters in them from colliding. Used by <c>PrintLogContext.UpdateTimestamps</c> (#256).
/// </summary>
internal sealed class EntityRowKey : IEquatable<EntityRowKey>
{
    private readonly Type rootType;
    private readonly object?[] values;

    internal EntityRowKey(Type rootType, object?[] values)
    {
        this.rootType = rootType;
        this.values = values;
    }

    public static EntityRowKey Of(EntityEntry entry) => new(
        entry.Metadata.GetRootType().ClrType,
        entry.Metadata.FindPrimaryKey()!.Properties.Select(p => entry.Property(p.Name).CurrentValue).ToArray());

    /// <summary>
    /// A lookup keeping the first value per key. A duplicate key never throws, so an unexpected
    /// pair can at worst miss a restore, never fail the save.
    /// </summary>
    public static Dictionary<EntityRowKey, T> IndexFirst<T>(IEnumerable<(EntityRowKey Key, T Value)> rows)
    {
        var index = new Dictionary<EntityRowKey, T>();
        foreach (var (key, value) in rows)
        {
            index.TryAdd(key, value);
        }

        return index;
    }

    public bool Equals(EntityRowKey? other) =>
        other is not null
        && rootType == other.rootType
        && StructuralComparisons.StructuralEqualityComparer.Equals(values, other.values);

    public override bool Equals(object? obj) => Equals(obj as EntityRowKey);

    public override int GetHashCode() =>
        HashCode.Combine(rootType, StructuralComparisons.StructuralEqualityComparer.GetHashCode(values));
}
