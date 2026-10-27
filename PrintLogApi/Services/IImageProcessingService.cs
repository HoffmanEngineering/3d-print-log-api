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
}

public interface IImageProcessingService
{
    Task<ProcessedImage> ProcessAsync(Stream input, CancellationToken ct = default);
}
