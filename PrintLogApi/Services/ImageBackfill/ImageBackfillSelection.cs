using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;

namespace PrintLogApi.Services.ImageBackfill;

/// <summary>What one image row still needs. Each flag is one piece of work the backfill can do.</summary>
public readonly record struct ImageBackfillPlan(bool ReplaceOriginal, bool AddThumbnail)
{
    public bool IsEmpty => !ReplaceOriginal && !AddThumbnail;
}

/// <summary>
/// Which image rows the backfill selects, and what it does to each. Adding a derivative (the
/// social image, say) is one more clause in <see cref="NeedsWork{T}"/>, one more flag in
/// <see cref="Plan"/>, and the step that writes it; the two must agree, or a row is selected
/// with nothing to do, or never selected at all.
/// </summary>
public static class ImageBackfillSelection
{
    /// <summary>The query-side form of <see cref="Plan"/>: true when the plan is not empty.</summary>
    public static Expression<Func<T, bool>> NeedsWork<T>() where T : class, IStoredImage
        => image => image.ContentType == null || image.ThumbnailFileId == null;

    /// <summary>
    /// A row with no content type was stored before uploads were processed: its original is the
    /// raw upload and gets re-encoded. A row with a type but no thumbnail is already canonical
    /// and only needs the derivative.
    /// </summary>
    public static ImageBackfillPlan Plan(IStoredImage image)
        => new(ReplaceOriginal: image.ContentType == null, AddThumbnail: image.ThumbnailFileId == null);

    /// <summary>The ids of the next rows needing work after <paramref name="afterId"/>, ascending.</summary>
    public static IQueryable<int> NextBatch<T>(IQueryable<T> scope, int afterId, int take)
        where T : class, IStoredImage
        => scope
            .Where(NeedsWork<T>())
            .Where(i => i.Id > afterId)
            .OrderBy(i => i.Id)
            .Select(i => i.Id)
            .Take(take);

    /// <summary>One row, tracked, with its original's File row.</summary>
    public static IQueryable<T> Row<T>(IQueryable<T> scope, int id)
        where T : class, IStoredImage
        => scope
            .Include(nameof(IStoredImage.File))
            .Where(i => i.Id == id);
}
