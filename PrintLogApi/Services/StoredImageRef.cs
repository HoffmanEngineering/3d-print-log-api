namespace PrintLogApi.Services;

/// <summary>
/// One print or project image blob an endpoint is about to serve: where it is, the type stored
/// for it, and whether a shared cache (Cloudflare, a proxy) may keep the response.
/// </summary>
/// <param name="FilePath">The File row's Path: "container/name" or, for older project rows, a bare name.</param>
/// <param name="ContentType">The type stored for the blob, or null for a legacy row (served by extension).</param>
/// <param name="SharedCacheable">
/// True when anyone may read the image without signing in (the print or project is not
/// private). Only then is the response marked <c>public</c>.
/// </param>
public sealed record StoredImageRef(string? FilePath, string? ContentType, bool SharedCacheable)
{
    /// <summary>
    /// The thumbnail when the row has one, else the original: a row uploaded before thumbnails
    /// existed, or whose thumbnail failed, still shows something.
    /// </summary>
    public static StoredImageRef ThumbnailOrOriginal(
        string? thumbnailPath, string? originalPath, string? originalContentType, bool sharedCacheable)
        => thumbnailPath is not null
            ? new StoredImageRef(thumbnailPath, ProcessedImageWriter.ThumbnailContentType, sharedCacheable)
            : new StoredImageRef(originalPath, originalContentType, sharedCacheable);
}
