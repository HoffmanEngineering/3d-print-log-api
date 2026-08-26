using SkiaSharp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

public class SkiaSharpSmokeTests
{
    [Fact]
    public void NativeLibrary_LoadsAndRoundTripsAnImage()
    {
        // Exercises the native libSkiaSharp binding. Restore succeeding proves only
        // that the managed assembly is present.
        using var bitmap = new SKBitmap(4, 4);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);

        Assert.NotNull(encoded);
        Assert.True(encoded.Size > 0);

        using var codec = SKCodec.Create(new MemoryStream(encoded.ToArray()));
        Assert.NotNull(codec);
        Assert.Equal(SKEncodedImageFormat.Png, codec!.EncodedFormat);
    }

    [Fact]
    public void WebpEncoding_IsAvailable()
    {
        // Thumbnails are WebP. Confirm the encoder is present in this native build.
        using var bitmap = new SKBitmap(4, 4);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, 80);

        Assert.NotNull(encoded);
        Assert.True(encoded.Size > 0);
    }
}
