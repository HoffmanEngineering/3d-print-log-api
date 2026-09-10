using System.ComponentModel;

namespace PrintLogApi.Models.DTOs.Printer;

/// <summary>
/// One printer's default photo, as it is safe to cache: an unsigned blob path plus the
/// content type to sign it with. Never reaches a browser.
/// </summary>
public class CachedDefaultImage
{
    public string? BlobPath { get; set; }

    /// <summary>
    /// "image/webp" when <see cref="BlobPath"/> is a thumbnail, the image's own type when
    /// there is none. Signing an original as WebP would set a response content type the
    /// bytes contradict.
    /// </summary>
    public string? ContentType { get; set; }
}

/// <summary>
/// What the printer summary endpoint caches: the page, plus the unsigned default-image path
/// for each printer on it.
/// </summary>
/// <remarks>
/// <para>The paths live HERE rather than on <see cref="PrinterSummarySimpleDto"/> for two
/// reasons. First, a signed URL must never be cached - it would outlive its signature - so
/// the cache has to hold the path and the request has to do the signing. Second, keeping the
/// path off the response DTO makes leaking it structurally impossible rather than a matter of
/// remembering a <c>[JsonIgnore]</c>: HybridCache round-trips values through
/// System.Text.Json, so a <c>[JsonIgnore]</c> member does not survive the cache anyway.</para>
///
/// <para>Marked immutable for HybridCache on the same terms as
/// <see cref="PagedList{T}"/>: treat anything read from the cache as read-only, and copy
/// before mutating.</para>
/// </remarks>
[ImmutableObject(true)]
public class CachedPrinterSummaryPage
{
    /// <summary>For deserialization only.</summary>
    public CachedPrinterSummaryPage()
    {
        Page = new PagedList<PrinterSummarySimpleDto>();
        DefaultImages = new Dictionary<long, CachedDefaultImage>();
    }

    public CachedPrinterSummaryPage(
        PagedList<PrinterSummarySimpleDto> page,
        Dictionary<long, CachedDefaultImage> defaultImages)
    {
        Page = page;
        DefaultImages = defaultImages;
    }

    public PagedList<PrinterSummarySimpleDto> Page { get; set; }

    /// <summary>Keyed by printer id. A printer with no photos has no entry.</summary>
    public Dictionary<long, CachedDefaultImage> DefaultImages { get; set; }
}
