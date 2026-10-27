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

    /// <summary>
    /// Link previews: 1200px is what Facebook, X, Slack and Discord ask for on the long edge, and
    /// JPEG is the one format all of them accept.
    /// </summary>
    private const int SocialLongEdgePx = 1200;

    /// <summary>
    /// Measured on three photos of prints, scaled to 900x1200: quality 82 gives 90-116 KB (75 gives
    /// 75-97 KB, 90 gives 121-157 KB). That leaves busier scenes well under the 300 KB some
    /// preview services cap at. Pure noise, the worst case no photo reaches, is about 700 KB.
    /// </summary>
    private const int SocialQuality = 82;

    private static readonly HashSet<SKEncodedImageFormat> AllowedFormats =
    [
        SKEncodedImageFormat.Jpeg,
        SKEncodedImageFormat.Png,
        SKEncodedImageFormat.Webp,
    ];

    /// <summary>Formats only the backfill accepts (<see cref="ImageProcessingOptions.LegacyBackfill"/>).</summary>
    private static readonly HashSet<SKEncodedImageFormat> LegacyFormats =
    [
        SKEncodedImageFormat.Gif,
        SKEncodedImageFormat.Bmp,
    ];

    public Task<ProcessedImage> ProcessAsync(Stream input, CancellationToken ct = default)
        => ProcessAsync(input, ImageProcessingOptions.Uploads, ct);

    public async Task<ProcessedImage> ProcessAsync(Stream input, ImageProcessingOptions options, CancellationToken ct = default)
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

        var legacyFormat = options.AcceptLegacyFormats && LegacyFormats.Contains(codec.EncodedFormat);
        if (!AllowedFormats.Contains(codec.EncodedFormat) && !legacyFormat)
            throw new InvalidImageException(
                $"Unsupported image format '{codec.EncodedFormat}'. Allowed: JPEG, PNG, WebP.");

        var info = codec.Info;

        if (info.Width <= 0 || info.Height <= 0)
            throw new InvalidImageException("The image has no usable dimensions.");

        if (info.Width > MaxEdgePx || info.Height > MaxEdgePx)
            throw new InvalidImageException($"Image edges must be {MaxEdgePx}px or smaller.");

        if ((long)info.Width * info.Height > MaxPixels)
            throw new InvalidImageException($"Image must be {MaxPixels / 1_000_000}MP or smaller.");

        // Catches animated WebP; GIF is already excluded by the format allowlist. The backfill
        // keeps the first frame instead, which is what the decode below reads.
        if (codec.FrameCount > 1 && !options.AcceptLegacyFormats)
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

        byte[]? social = null;
        int? socialWidth = null, socialHeight = null;
        if (options.IncludeSocial)
        {
            try
            {
                using var scaled = ScaleLongEdge(oriented, SocialLongEdgePx);
                social = Encode(scaled, SKEncodedImageFormat.Jpeg, SocialQuality);
                if (social is not null)
                {
                    socialWidth = scaled.Width;
                    socialHeight = scaled.Height;
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Same reasoning as the thumbnail: the original is fine, and the social-image
                // endpoint falls back to it.
                social = null;
            }
        }

        return new ProcessedImage(original, "image/jpeg", thumbnail)
        {
            Width = oriented.Width,
            Height = oriented.Height,
            SourceContentType = SourceContentTypeOf(codec.EncodedFormat),
            Social = social,
            SocialWidth = socialWidth,
            SocialHeight = socialHeight,
        };
    }

    private static string? SourceContentTypeOf(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Jpeg => "image/jpeg",
        SKEncodedImageFormat.Png => "image/png",
        SKEncodedImageFormat.Webp => "image/webp",
        SKEncodedImageFormat.Gif => "image/gif",
        SKEncodedImageFormat.Bmp => "image/bmp",
        _ => null,
    };

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
    private static SKBitmap? CreateThumbnail(SKBitmap source) => ScaleLongEdge(source, ThumbnailLongEdgePx);

    /// <summary>A copy of <paramref name="source"/> with its long edge at most <paramref name="longEdgePx"/>.</summary>
    private static SKBitmap ScaleLongEdge(SKBitmap source, int longEdgePx)
    {
        var longEdge = Math.Max(source.Width, source.Height);
        if (longEdge <= longEdgePx)
            return source.Copy();

        var scale = (double)longEdgePx / longEdge;
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
