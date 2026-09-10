using PrintLogApi.Services;

namespace PrintLogApi.IntegrationTests;

/// <summary>
/// Wraps the real <see cref="ImageProcessingService"/> so tests can force the
/// thumbnail-generation-failed branch, which is otherwise unreachable: the real encoder
/// does not fail on a valid image, and that branch decides which content type gets signed.
/// </summary>
public class ControllableImageProcessingService(ImageProcessingService inner) : IImageProcessingService
{
    /// <summary>
    /// When set, the NEXT successful process drops its thumbnail and resets this flag. A
    /// sticky flag would leak into whatever ran next in the same class fixture.
    /// </summary>
    public bool NextThumbnailIsNull { get; set; }

    public async Task<ProcessedImage> ProcessAsync(Stream input, CancellationToken ct = default)
    {
        var processed = await inner.ProcessAsync(input, ct);

        if (!NextThumbnailIsNull) return processed;

        NextThumbnailIsNull = false;
        return processed with { Thumbnail = null };
    }
}
