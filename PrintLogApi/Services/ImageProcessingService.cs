using PrintLogApi.Exceptions;
using SkiaSharp;

namespace PrintLogApi.Services;

public class ImageProcessingService : IImageProcessingService
{
    private const int MaxEdgePx = 12_000;
    private const long MaxPixels = 50_000_000;
    private const int ThumbnailLongEdgePx = 320;
    private const int OriginalQuality = 90;
    private const int ThumbnailQuality = 80;

    private static readonly HashSet<SKEncodedImageFormat> AllowedFormats =
    [
        SKEncodedImageFormat.Jpeg,
        SKEncodedImageFormat.Png,
        SKEncodedImageFormat.Webp,
    ];

    public async Task<ProcessedImage> ProcessAsync(Stream input, CancellationToken ct = default)
    {
        // Buffer once: SKCodec needs a seekable source and we read it twice.
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, ct);
        if (buffer.Length == 0)
            throw new InvalidImageException("The file is empty.");
        buffer.Position = 0;

        using var data = SKData.CreateCopy(buffer.ToArray());

        // ---- Header preflight. Nothing below decodes pixels. ----
        // This ordering is load-bearing: SkiaSharp has no allocation limiter, so
        // these checks are the ONLY defense against a decompression bomb.
        using var codec = SKCodec.Create(data);
        if (codec is null)
            throw new InvalidImageException("The file could not be read as an image.");

        if (!AllowedFormats.Contains(codec.EncodedFormat))
            throw new InvalidImageException(
                $"Unsupported image format '{codec.EncodedFormat}'. Allowed: JPEG, PNG, WebP.");

        var info = codec.Info;

        if (info.Width <= 0 || info.Height <= 0)
            throw new InvalidImageException("The image has no usable dimensions.");

        if (info.Width > MaxEdgePx || info.Height > MaxEdgePx)
            throw new InvalidImageException($"Image edges must be {MaxEdgePx}px or smaller.");

        if ((long)info.Width * info.Height > MaxPixels)
            throw new InvalidImageException($"Image must be {MaxPixels / 1_000_000}MP or smaller.");

        // Catches animated WebP; GIF is already excluded by the format allowlist.
        if (codec.FrameCount > 1)
            throw new InvalidImageException("Animated images are not supported.");

        // Captured before decode: SKBitmap.Decode discards it.
        var origin = codec.EncodedOrigin;

        // ---- Decode. Preflight has passed. ----
        using var decoded = SKBitmap.Decode(codec);
        if (decoded is null)
            throw new InvalidImageException("The image could not be decoded.");

        // SkiaSharp encoders write no EXIF, so GPS and every other tag are gone by
        // construction. Orientation goes with them, so apply it here or portrait
        // photos land sideways.
        using var oriented = ApplyOrigin(decoded, origin);

        var original = Encode(oriented, SKEncodedImageFormat.Jpeg, OriginalQuality)
            ?? throw new InvalidImageException("The image could not be re-encoded.");

        byte[]? thumbnail;
        try
        {
            using var thumb = CreateThumbnail(oriented);
            thumbnail = thumb is null
                ? null
                : Encode(thumb, SKEncodedImageFormat.Webp, ThumbnailQuality);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Deliberate: the original decoded cleanly, so it is safe to store. Only
            // the derivative failed; the DTO falls back to the original's URL.
            thumbnail = null;
        }

        return new ProcessedImage(original, "image/jpeg", thumbnail);
    }

    /// <summary>
    /// Applies an EXIF origin to pixels. SkiaSharp does not do this during decode,
    /// and re-encoding drops the tag, so skipping it rotates every phone photo.
    /// </summary>
    private static SKBitmap ApplyOrigin(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.Default or SKEncodedOrigin.TopLeft)
            return source.Copy();

        var swapsAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

        var width = swapsAxes ? source.Height : source.Width;
        var height = swapsAxes ? source.Width : source.Height;

        var target = new SKBitmap(width, height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(target);

        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Translate(width, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.RotateDegrees(180, width / 2f, height / 2f);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, height);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(width, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(width, 0);
                canvas.RotateDegrees(90);
                canvas.Translate(source.Width, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, height);
                canvas.RotateDegrees(-90);
                break;
        }

        canvas.DrawBitmap(source, 0, 0);
        canvas.Flush();
        return target;
    }

    /// <summary>
    /// Scales the long edge down to the thumbnail size. Never upscales: a 100px
    /// photo blown up to 320px is a bigger file that looks worse.
    /// </summary>
    private static SKBitmap? CreateThumbnail(SKBitmap source)
    {
        var longEdge = Math.Max(source.Width, source.Height);
        if (longEdge <= ThumbnailLongEdgePx)
            return source.Copy();

        var scale = (double)ThumbnailLongEdgePx / longEdge;
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        return source.Resize(
            new SKImageInfo(width, height, source.ColorType, source.AlphaType),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }

    private static byte[]? Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, quality);
        return encoded?.ToArray();
    }
}
