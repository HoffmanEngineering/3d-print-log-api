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

    [Fact]
    public async Task Gif_IsRejectedForUploads()
    {
        await using var gif = Fixture("legacy-animated.gif");

        var ex = await Assert.ThrowsAsync<InvalidImageException>(
            () => _service.ProcessAsync(gif, TestContext.Current.CancellationToken));

        Assert.Contains("Unsupported image format", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyFormats_AcceptBmp_AndReportTheSourceType()
    {
        using var bmp = Bmp24(50, 40);

        var result = await _service.ProcessAsync(
            bmp, ImageProcessingOptions.LegacyBackfill, TestContext.Current.CancellationToken);

        Assert.Equal("image/jpeg", result.ContentType);
        Assert.Equal("image/bmp", result.SourceContentType);
        Assert.Equal(50, result.Width);
        Assert.Equal(40, result.Height);
        Assert.NotNull(result.Thumbnail);
    }

    [Fact]
    public async Task LegacyFormats_TakeTheFirstFrameOfAnAnimatedGif()
    {
        // Two frames: red, then blue.
        await using var gif = Fixture("legacy-animated.gif");

        var result = await _service.ProcessAsync(
            gif, ImageProcessingOptions.LegacyBackfill, TestContext.Current.CancellationToken);

        Assert.Equal("image/gif", result.SourceContentType);
        Assert.Equal(64, result.Width);
        Assert.Equal(48, result.Height);
        using var decoded = SKBitmap.Decode(result.Original);
        var pixel = decoded.GetPixel(32, 24);
        Assert.True(pixel.Red > 200 && pixel.Blue < 60, $"expected the red first frame, got {pixel}");
    }

    [Fact]
    public async Task UploadOptions_ReportTheSourceType()
    {
        using var png = Encode(100, 50, SKEncodedImageFormat.Png);

        var result = await _service.ProcessAsync(png, TestContext.Current.CancellationToken);

        Assert.Equal("image/png", result.SourceContentType);
    }

    [Fact]
    public async Task PrintUploads_AddASocialJpeg_ScaledTo1200OnTheLongEdge()
    {
        using var png = Encode(3000, 1500, SKEncodedImageFormat.Png);

        var result = await _service.ProcessAsync(
            png, ImageProcessingOptions.PrintUploads, TestContext.Current.CancellationToken);

        Assert.NotNull(result.Social);
        using var codec = SKCodec.Create(new MemoryStream(result.Social!));
        Assert.Equal(SKEncodedImageFormat.Jpeg, codec!.EncodedFormat);
        Assert.Equal(1200, codec.Info.Width);
        Assert.Equal(600, codec.Info.Height);
        Assert.Equal(1200, result.SocialWidth);
        Assert.Equal(600, result.SocialHeight);
    }

    [Fact]
    public async Task PrintUploads_DoNotUpscaleASmallImageIntoASocialImage()
    {
        using var png = Encode(640, 480, SKEncodedImageFormat.Png);

        var result = await _service.ProcessAsync(
            png, ImageProcessingOptions.PrintUploads, TestContext.Current.CancellationToken);

        using var codec = SKCodec.Create(new MemoryStream(result.Social!));
        Assert.Equal(640, codec!.Info.Width);
        Assert.Equal(480, codec.Info.Height);
        Assert.Equal(640, result.SocialWidth);
        Assert.Equal(480, result.SocialHeight);
    }

    [Fact]
    public async Task PrintUploads_SocialImageFollowsTheAppliedOrientation()
    {
        await using var input = Fixture("exif-orientation-6.jpg");

        var result = await _service.ProcessAsync(
            input, ImageProcessingOptions.PrintUploads, TestContext.Current.CancellationToken);

        Assert.Equal(50, result.SocialWidth);
        Assert.Equal(100, result.SocialHeight);
    }

    [Fact]
    public async Task DefaultUploads_ProduceNoSocialImage()
    {
        // Printer and material images go through the default options and never use one.
        using var png = Encode(3000, 1500, SKEncodedImageFormat.Png);

        var result = await _service.ProcessAsync(png, TestContext.Current.CancellationToken);

        Assert.Null(result.Social);
        Assert.Null(result.SocialWidth);
        Assert.Null(result.SocialHeight);
    }

    [Fact]
    public async Task LegacyBackfill_AlsoProducesASocialImage()
    {
        using var png = Encode(2400, 1800, SKEncodedImageFormat.Png);

        var result = await _service.ProcessAsync(
            png, ImageProcessingOptions.LegacyBackfill, TestContext.Current.CancellationToken);

        Assert.Equal(1200, result.SocialWidth);
        Assert.Equal(900, result.SocialHeight);
    }

    [Fact]
    public async Task EveryDecode_ReturnsWhatItReservedFromTheGate_WhateverTheOutcome()
    {
        var gate = new ImageMemoryGate(ImageMemoryGate.DefaultBudgetBytes, TimeSpan.FromSeconds(5));
        var service = new ImageProcessingService(gate);

        using (var plain = Encode(800, 600, SKEncodedImageFormat.Png))
        {
            await service.ProcessAsync(plain, ImageProcessingOptions.PrintUploads, TestContext.Current.CancellationToken);
        }

        await using (var rotated = Fixture("exif-orientation-6.jpg"))
        {
            await service.ProcessAsync(rotated, TestContext.Current.CancellationToken);
        }

        using (var oversized = Encode(13000, 4, SKEncodedImageFormat.Jpeg))
        {
            await Assert.ThrowsAsync<InvalidImageException>(
                () => service.ProcessAsync(oversized, TestContext.Current.CancellationToken));
        }

        Assert.Equal(gate.BudgetBytes, gate.AvailableBytes);
    }

    [Fact]
    public async Task WhileTheBudgetIsSpent_ADecodeWaits_ThenGivesUpAsBusy()
    {
        var gate = new ImageMemoryGate(64L * 1024 * 1024, TimeSpan.FromMilliseconds(100));
        var service = new ImageProcessingService(gate);
        using var held = await gate.AcquireAsync(gate.BudgetBytes, TestContext.Current.CancellationToken);
        using var input = Encode(800, 600, SKEncodedImageFormat.Png);

        await Assert.ThrowsAsync<ImageProcessingBusyException>(
            () => service.ProcessAsync(input, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ASmallSource_IsEncodedAsItIs_ForTheThumbnailAndSocialImage()
    {
        // Below both derivative sizes nothing is scaled: each is encoded from the oriented
        // bitmap itself, at the source's own size.
        using var input = Encode(200, 100, SKEncodedImageFormat.Png);

        var result = await _service.ProcessAsync(input, ImageProcessingOptions.PrintUploads, TestContext.Current.CancellationToken);

        Assert.NotNull(result.Thumbnail);
        using var thumb = SKCodec.Create(new MemoryStream(result.Thumbnail!));
        Assert.Equal((200, 100), (thumb.Info.Width, thumb.Info.Height));
        Assert.Equal(200, result.SocialWidth);
        Assert.Equal(100, result.SocialHeight);
    }
}
