namespace PrintLogApi.Services;

/// <summary>Result of validating and normalizing an uploaded image.</summary>
/// <param name="Original">Re-encoded original, orientation applied, metadata absent.</param>
/// <param name="ContentType">Authoritative MIME type of the original.</param>
/// <param name="Thumbnail">320px WebP derivative, or null if generation failed.</param>
public record ProcessedImage(byte[] Original, string ContentType, byte[]? Thumbnail)
{
    /// <summary>Pixel width of <see cref="Original"/>, after orientation was applied.</summary>
    public int Width { get; init; }

    /// <summary>Pixel height of <see cref="Original"/>, after orientation was applied.</summary>
    public int Height { get; init; }

    /// <summary>MIME type of the input as decoded (for example image/png), before re-encoding.</summary>
    public string? SourceContentType { get; init; }
}

/// <summary>Which inputs <see cref="IImageProcessingService"/> accepts.</summary>
public sealed record ImageProcessingOptions
{
    /// <summary>What every upload endpoint accepts: JPEG, PNG and WebP, single frame.</summary>
    public static ImageProcessingOptions Uploads { get; } = new();

    /// <summary>
    /// For the image backfill only: also accepts GIF and BMP, which uploads allowed before they
    /// were processed, and takes the first frame of an animated image. Never use it for uploads.
    /// </summary>
    public static ImageProcessingOptions LegacyBackfill { get; } = new() { AcceptLegacyFormats = true };

    public bool AcceptLegacyFormats { get; init; }
}

public interface IImageProcessingService
{
    /// <summary>Processes an upload, with <see cref="ImageProcessingOptions.Uploads"/>.</summary>
    Task<ProcessedImage> ProcessAsync(Stream input, CancellationToken ct = default);

    Task<ProcessedImage> ProcessAsync(Stream input, ImageProcessingOptions options, CancellationToken ct = default);
}
