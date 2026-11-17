using Microsoft.EntityFrameworkCore;

namespace PrintLogApi.Services;

/// <summary>
/// For a full PUT that maps a request's child rows over the collection of a tracked parent
/// (a print's usage rows, a spool's adjustments).
/// <para>
/// AutoMapper builds a new instance for every row in the request, keeping the id the client sent.
/// EF Core then takes a discovered row whose generated key is already set for an existing row
/// and saves it as an UPDATE by that key, whichever parent it belongs to. So a row id another
/// user's parent owns (readable from any public print) moved that row onto the caller's parent,
/// and an id no row has any more (a stale edit form) failed the save with a concurrency error.
/// </para>
/// <para>
/// The rule here: a row keeps its id only when the parent already had that id, once. Every other
/// row, including one sent with an empty id, is a new row of the parent's own.
/// </para>
/// </summary>
internal static class ChildRows
{
    /// <summary>The ids of the parent's current rows. Take them before the map replaces the rows.</summary>
    public static HashSet<Guid> IdsOf<T>(IEnumerable<T>? rows, Func<T, Guid> id) =>
        (rows ?? []).Select(id).ToHashSet();

    /// <summary>
    /// Gives every mapped row whose id is not one of <paramref name="ownIds"/> (or repeats one
    /// already kept) a fresh id and tracks it as Added, so it is inserted rather than updating
    /// whatever row the client named. <paramref name="makeNew"/> sets the fresh id and the row's
    /// foreign key to the parent.
    /// <para>
    /// Kept rows are left alone, foreign key included: the parent's collection fixes it up, and
    /// setting it before change detection would change the original values the achievement
    /// trigger extractor reads off those rows.
    /// </para>
    /// </summary>
    public static void AddUnowned<T>(
        DbContext context, IEnumerable<T> mappedRows, IReadOnlySet<Guid> ownIds,
        Func<T, Guid> getId, Action<T, Guid> makeNew)
        where T : class
    {
        var kept = new HashSet<Guid>();
        foreach (var row in mappedRows)
        {
            var id = getId(row);
            if (ownIds.Contains(id) && kept.Add(id))
            {
                continue;
            }

            makeNew(row, Guid.NewGuid());
            // Added explicitly: found through the navigation with its key set, it would be
            // taken for an existing row (see ReplaceUsageRows in PrintService).
            context.Add(row);
        }
    }
}
