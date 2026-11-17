using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using PrintLogApi.Services;

namespace PrintLogApi.Extensions;

/// <summary>
/// Builds the response for the print and project image endpoints: the stored content type when
/// it is a raster image type, otherwise a download, always with headers that keep the browser
/// from reinterpreting the bytes. Streams from blob storage with an ETag, answers a matching
/// <c>If-None-Match</c> with 304 before touching storage, and supports range requests.
/// </summary>
public static class ImageFileResult
{
    public const string ContentSecurityPolicy = "default-src 'none'; sandbox";

    /// <summary>
    /// Shared caches (Cloudflare in front of the site) may keep an image anyone can read for an
    /// hour. Kept short on purpose: a print or project made private drops out of every shared
    /// cache within this window, and the ETag makes revalidation after it cheap.
    /// </summary>
    public static readonly TimeSpan SharedMaxAge = TimeSpan.FromHours(1);

    /// <summary>
    /// The owner's own browser may keep an image of a private print or project as long as it did
    /// before streaming (7 days); <c>private</c> keeps it out of shared caches.
    /// </summary>
    public static readonly TimeSpan PrivateMaxAge = TimeSpan.FromDays(7);

    /// <summary>Serves <paramref name="image"/> from <paramref name="containerName"/>.</summary>
    public static async Task<IActionResult> ServeAsync(
        ControllerBase controller, IBlobStorageService blobStorage, string containerName,
        StoredImageRef image, CancellationToken ct)
    {
        var blobName = Path.GetFileName(image.FilePath);
        if (string.IsNullOrEmpty(blobName))
            return controller.NotFound();

        var request = controller.Request;
        var headers = controller.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy = ContentSecurityPolicy;

        var entityTag = EntityTagFor(image.FilePath!);
        if (MatchesIfNoneMatch(request, entityTag))
        {
            SetCacheHeaders(controller.Response, image.SharedCacheable, entityTag);
            return controller.StatusCode(StatusCodes.Status304NotModified);
        }

        // Only a range request needs a seekable stream; everything else is one forward-only GET.
        var wantsRange = request.Headers.Range.Count > 0;
        var content = await blobStorage.OpenReadAsync(containerName, blobName, seekable: wantsRange, ct);
        if (content is null)
            return controller.NotFound();

        SetCacheHeaders(controller.Response, image.SharedCacheable, entityTag);

        // A seekable stream lets FileStreamResult answer ranges and set Content-Length itself.
        // A forward-only one does not, so say both here; ranges then come back seekable.
        var rangeable = content.Content.CanSeek;
        if (!rangeable)
        {
            controller.Response.ContentLength = content.Length;
            headers.AcceptRanges = "bytes";
        }

        var servedType = ImageContentTypes.ResolveServedType(image.ContentType, blobName);
        if (servedType is not null)
            return controller.File(content.Content, servedType, lastModified: null, entityTag, rangeable);

        // Setting a download name is what makes FileStreamResult emit
        // "Content-Disposition: attachment".
        var downloadName = Path.GetFileNameWithoutExtension(blobName);
        return controller.File(
            content.Content, ImageContentTypes.OctetStream,
            string.IsNullOrEmpty(downloadName) ? "image" : downloadName,
            lastModified: null, entityTag, rangeable);
    }

    /// <summary>
    /// A strong tag from the blob's path. Every upload and every backfill write gets a fresh blob
    /// name and blobs are never rewritten in place, so the path identifies the bytes. The File id
    /// would not: the backfill points an existing File row at a re-encoded blob. Strong, because
    /// images are never compressed in transit, and range requests need a strong validator.
    /// </summary>
    public static EntityTagHeaderValue EntityTagFor(string filePath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(filePath));
        return new EntityTagHeaderValue($"\"{Convert.ToHexStringLower(hash.AsSpan(0, 12))}\"");
    }

    private static bool MatchesIfNoneMatch(HttpRequest request, EntityTagHeaderValue entityTag)
    {
        if (!EntityTagHeaderValue.TryParseList(request.Headers.IfNoneMatch, out var tags))
            return false;
        return tags.Any(t => t.Equals(EntityTagHeaderValue.Any) || t.Compare(entityTag, useStrongComparison: false));
    }

    private static void SetCacheHeaders(HttpResponse response, bool sharedCacheable, EntityTagHeaderValue entityTag)
    {
        response.GetTypedHeaders().CacheControl = sharedCacheable
            ? new CacheControlHeaderValue { Public = true, MaxAge = SharedMaxAge }
            : new CacheControlHeaderValue { Private = true, MaxAge = PrivateMaxAge };
        response.Headers.ETag = entityTag.ToString();
    }
}
