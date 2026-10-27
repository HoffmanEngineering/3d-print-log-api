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

    /// <summary>
    /// The link-preview derivative: a JPEG of about 1200px on the long edge, never upscaled.
    /// Null unless <see cref="ImageProcessingOptions.IncludeSocial"/> was set, or when its
    /// generation failed.
    /// </summary>
    public byte[]? Social { get; init; }

    /// <summary>Pixel width of <see cref="Social"/>; null when there is none.</summary>
    public int? SocialWidth { get; init; }

    /// <summary>Pixel height of <see cref="Social"/>; null when there is none.</summary>
    public int? SocialHeight { get; init; }
}

/// <summary>Which inputs <see cref="IImageProcessingService"/> accepts.</summary>
public sealed record ImageProcessingOptions
{
    /// <summary>What every upload endpoint accepts: JPEG, PNG and WebP, single frame.</summary>
    public static ImageProcessingOptions Uploads { get; } = new();

    /// <summary>
    /// Print and project uploads (and printer snapshots): <see cref="Uploads"/> plus the social
    /// derivative their public pages use for link previews. Printer and material images have
    /// no public page, so they keep <see cref="Uploads"/> and skip the extra encode.
    /// </summary>
    public static ImageProcessingOptions PrintUploads { get; } = new() { IncludeSocial = true };

    /// <summary>
    /// For the image backfill only: also accepts GIF and BMP, which uploads allowed before they
    /// were processed, and takes the first frame of an animated image. Never use it for uploads.
    /// </summary>
    public static ImageProcessingOptions LegacyBackfill { get; } = new() { AcceptLegacyFormats = true, IncludeSocial = true };

    public bool AcceptLegacyFormats { get; init; }

    /// <summary>Also produce <see cref="ProcessedImage.Social"/>.</summary>
    public bool IncludeSocial { get; init; }
}

public interface IImageProcessingService
{
    /// <summary>Processes an upload, with <see cref="ImageProcessingOptions.Uploads"/>.</summary>
    Task<ProcessedImage> ProcessAsync(Stream input, CancellationToken ct = default);

    Task<ProcessedImage> ProcessAsync(Stream input, ImageProcessingOptions options, CancellationToken ct = default);
}
