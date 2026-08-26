using PrintLogApi.Exceptions;
using PrintLogApi.Services;
using SkiaSharp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

public class ImageProcessingServiceTests
{
    private readonly ImageProcessingService _service = new();

    private static MemoryStream Encode(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        var ms = new MemoryStream(data.ToArray());
        ms.Position = 0;
        return ms;
    }

    private static Stream Fixture(string name)
        => File.OpenRead(Path.Combine(AppContext.BaseDirectory, "tests-fixtures", name));

    /// <summary>
    /// A hand-built 24-bit BMP. SkiaSharp has no BMP *encoder* in this native build
    /// (SKImage.Encode returns null for Bmp), so the bytes have to be written out by
    /// hand. That is the better test anyway: SkiaSharp still decodes these, so the
    /// rejection proves the format allowlist and not merely a failed decode.
    /// </summary>
    private static MemoryStream Bmp24(int width, int height)
    {
        var rowStride = (width * 3 + 3) / 4 * 4;
        var pixelBytes = rowStride * height;
        var buffer = new byte[54 + pixelBytes];
        var writer = new BinaryWriter(new MemoryStream(buffer));

        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(buffer.Length);
        writer.Write(0);
        writer.Write(54);   // pixel data offset

        writer.Write(40);   // BITMAPINFOHEADER size
        writer.Write(width);
        writer.Write(height);
        writer.Write((short)1);
        writer.Write((short)24);
        writer.Write(0);    // BI_RGB
        writer.Write(pixelBytes);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                writer.Write((byte)0x20);
                writer.Write((byte)0x60);
                writer.Write((byte)0xA0);
            }

            for (var pad = width * 3; pad < rowStride; pad++)
                writer.Write((byte)0);
        }

        writer.Flush();
        return new MemoryStream(buffer);
    }

    [Fact]
    public async Task UndecodableBytes_AreRejected()
    {
        // Arbitrary bytes a client could label "image/png". Decoding IS the
        // validation; storing these would make the endpoint arbitrary blob storage.
        // SKCodec.Create returns null here rather than throwing.
        using var junk = new MemoryStream("not an image at all"u8.ToArray());

        await Assert.ThrowsAsync<InvalidImageException>(
            () => _service.ProcessAsync(junk, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmptyStream_IsRejected()
    {
        using var empty = new MemoryStream();

        await Assert.ThrowsAsync<InvalidImageException>(
            () => _service.ProcessAsync(empty, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OversizedDimensions_AreRejectedFromHeadersAlone()
    {
        // SkiaSharp has no allocation limiter, so this rejection must happen from
        // the header preflight, before any pixel decode. Encoding a real 13000px
        // image would be slow, so assert against a codec-visible header instead:
        // a valid JPEG whose declared dimensions exceed the cap.
        using var oversized = Encode(13000, 4, SKEncodedImageFormat.Jpeg);

        await Assert.ThrowsAsync<InvalidImageException>(
            () => _service.ProcessAsync(oversized, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Bmp_IsRejected()
    {
        // Dropped from the project-image allowlist: no use case for a spool photo.
        using var bmp = Bmp24(50, 50);

        // SkiaSharp decodes BMP happily, so this rejection can only come from the
        // allowlist. Asserting on the message pins that, rather than accepting a
        // rejection that a broken decode would also produce.
        var ex = await Assert.ThrowsAsync<InvalidImageException>(
            () => _service.ProcessAsync(bmp, TestContext.Current.CancellationToken));

        Assert.Contains("Unsupported image format", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidPng_IsAcceptedAndThumbnailed()
    {
        using var png = Encode(1000, 500, SKEncodedImageFormat.Png);

        var result = await _service.ProcessAsync(png, TestContext.Current.CancellationToken);

        Assert.Equal("image/jpeg", result.ContentType); // canonical re-encode
        Assert.NotNull(result.Thumbnail);

        using var thumbCodec = SKCodec.Create(new MemoryStream(result.Thumbnail!));
        Assert.NotNull(thumbCodec);
        Assert.Equal(SKEncodedImageFormat.Webp, thumbCodec!.EncodedFormat);
        Assert.Equal(320, thumbCodec.Info.Width);   // long edge
        Assert.Equal(160, thumbCodec.Info.Height);  // aspect preserved
    }

    [Fact]
    public async Task SmallImage_IsNotUpscaledIntoAThumbnail()
    {
        using var small = Encode(100, 80, SKEncodedImageFormat.Png);

        var result = await _service.ProcessAsync(small, TestContext.Current.CancellationToken);

        using var thumbCodec = SKCodec.Create(new MemoryStream(result.Thumbnail!));
        Assert.Equal(100, thumbCodec!.Info.Width);
        Assert.Equal(80, thumbCodec.Info.Height);
    }

    [Fact]
    public async Task ExifOrientation_IsAppliedThenDiscarded()
    {
        // SkiaSharp decodes to raw pixels and its encoders write no EXIF, so GPS
        // stripping is structural. The flip side is that orientation is discarded
        // too, so it must be applied manually or portrait photos land sideways.
        // The fixture is 100x50 with Orientation=6 (rotate 90 CW), so a correctly
        // oriented result is 50x100.
        await using var input = Fixture("exif-orientation-6.jpg");

        var result = await _service.ProcessAsync(input, TestContext.Current.CancellationToken);

        using var codec = SKCodec.Create(new MemoryStream(result.Original));
        Assert.NotNull(codec);
        Assert.Equal(50, codec!.Info.Width);
        Assert.Equal(100, codec.Info.Height);

        // No residual orientation metadata to re-apply.
        Assert.Equal(SKEncodedOrigin.TopLeft, codec.EncodedOrigin);
    }

    [Fact]
    public async Task GpsMetadata_DoesNotSurviveTheReEncode()
    {
        // The fixture carries a GPS IFD. Stripping it is the point: a spool photo
        // taken at home should not publish the photographer's coordinates.
        await using var input = Fixture("exif-orientation-6.jpg");

        var result = await _service.ProcessAsync(input, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            "Exif",
            System.Text.Encoding.ASCII.GetString(result.Original),
            StringComparison.Ordinal);
    }
}
