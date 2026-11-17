using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;

namespace PrintLogApi.Services;

/// <summary>
/// The image a public print or project shows in link previews: its default image (or, when none
/// is flagged, the first by display order), as the social derivative when it has one and as the
/// original otherwise. Shared by the social-image endpoints and <c>/seo</c>, so the URL the
/// metadata advertises and the bytes the endpoint serves always describe the same image.
/// </summary>
public sealed class SocialImageLookup(PrintLogContext context)
{
    /// <summary>Null unless the print is Public and has an image.</summary>
    public Task<SocialImage?> ForPrintAsync(long printId, CancellationToken ct = default)
        => context.PrintImages
            .AsNoTracking()
            .Where(pi => pi.PrintId == printId && pi.Print.ViewStatus == Print.PrintViewStatus.Public)
            .OrderByDescending(pi => pi.IsDefault)
            .ThenBy(pi => pi.DisplayOrder)
            .ThenBy(pi => pi.Id)
            .Select(pi => new SocialImage(
                pi.FileId, pi.File.Path, pi.ContentType, pi.Width, pi.Height,
                pi.SocialFileId, pi.SocialFile != null ? pi.SocialFile.Path : null, pi.SocialWidth, pi.SocialHeight))
            .FirstOrDefaultAsync(ct);

    /// <summary>Null unless the project is Public and has an image.</summary>
    public Task<SocialImage?> ForProjectAsync(Guid projectId, CancellationToken ct = default)
        => context.ProjectImages
            .AsNoTracking()
            .Where(pi => pi.ProjectId == projectId
                && pi.Project.ViewStatus == Project.PubliclyListedViewStatus)
            .OrderByDescending(pi => pi.IsDefault)
            .ThenBy(pi => pi.DisplayOrder)
            .ThenBy(pi => pi.Id)
            .Select(pi => new SocialImage(
                pi.FileId, pi.File.Path, pi.ContentType, pi.Width, pi.Height,
                pi.SocialFileId, pi.SocialFile != null ? pi.SocialFile.Path : null, pi.SocialWidth, pi.SocialHeight))
            .FirstOrDefaultAsync(ct);
}

/// <summary>A public print's or project's link-preview image. See <see cref="SocialImageLookup"/>.</summary>
public sealed record SocialImage(
    Guid FileId, string? OriginalPath, string? OriginalContentType, int? OriginalWidth, int? OriginalHeight,
    Guid? SocialFileId, string? SocialPath, int? SocialWidth, int? SocialHeight)
{
    private bool HasSocial => SocialFileId is not null && SocialPath is not null;

    /// <summary>What the social-image endpoint serves. Always shared-cacheable: the owner is Public.</summary>
    public StoredImageRef Served => HasSocial
        ? new StoredImageRef(SocialPath, ProcessedImageWriter.SocialContentType, SharedCacheable: true)
        : new StoredImageRef(OriginalPath, OriginalContentType, SharedCacheable: true);

    public int? Width => HasSocial ? SocialWidth : OriginalWidth;

    public int? Height => HasSocial ? SocialHeight : OriginalHeight;

    /// <summary>
    /// The <c>?v=</c> cache-busting token: the id of the File served. Choosing another default
    /// image, or the backfill adding a social file, changes it, so link-preview services that
    /// cache by URL fetch the new image.
    /// </summary>
    public string Version => (HasSocial ? SocialFileId!.Value : FileId).ToString("N")[..12];
}
