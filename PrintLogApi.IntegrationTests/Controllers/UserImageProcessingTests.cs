using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models.DTOs.User;
using PrintLogApi.Services;
using SkiaSharp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Profile and cover images go through the same decode and re-encode as every other image upload:
/// the stored blob carries no metadata (a phone photo's EXIF, GPS included), is named with the
/// extension of its decoded type rather than the client's file name, and anything that does not
/// decode as an image is refused whatever content type the client declared.
/// </summary>
public class UserImageProcessingTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private const string Container = "userprofile";

    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private InMemoryBlobStorageService Blobs
        => (InMemoryBlobStorageService)factory.Services.GetRequiredService<IBlobStorageService>();

    public static TheoryData<string> Endpoints => new() { "profile-image", "cover-image" };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Upload_ReEncodesAndRemovesMetadata(string endpoint)
    {
        var response = await UploadAsync(endpoint, Fixture("exif-orientation-6.jpg"), "image/jpeg", "photo.jpg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var blobPath = BlobPathOf(await response.Content.ReadFromJsonAsync<UserUrlDto>(Ct));
        var stored = Blobs.Blobs[blobPath];
        Assert.DoesNotContain("Exif", System.Text.Encoding.ASCII.GetString(stored), StringComparison.Ordinal);
        using var codec = SKCodec.Create(new MemoryStream(stored));
        Assert.Equal(SKEncodedImageFormat.Jpeg, codec!.EncodedFormat);
        // Orientation 6 on a 100x50 fixture: the stored image is upright, 50x100.
        Assert.Equal(50, codec.Info.Width);
        Assert.Equal(100, codec.Info.Height);
        Assert.Equal(stored.LongLength, await FileSizeAsync(blobPath));
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Upload_NamesBlobWithServerChosenExtension_NotClientFileName(string endpoint)
    {
        var response = await UploadAsync(endpoint, PngBytes(), "image/png", "avatar.html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var blobPath = BlobPathOf(await response.Content.ReadFromJsonAsync<UserUrlDto>(Ct));
        // Opaque, so the processor re-encodes it as JPEG.
        Assert.EndsWith(".jpg", blobPath, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Upload_RefusesBytesThatAreNotAnImage_WhateverTheDeclaredType(string endpoint)
    {
        var before = Blobs.UploadedBlobNames.Count;
        var html = System.Text.Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>");

        var response = await UploadAsync(endpoint, html, "image/png", "avatar.html");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, Blobs.UploadedBlobNames.Count);
    }

    // ---- Helpers ----

    private Task<HttpResponseMessage> UploadAsync(string endpoint, byte[] bytes, string contentType, string fileName)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/Users/me/{endpoint}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(content, "image", fileName);
        request.Content = form;
        return _client.SendAsync(request, Ct);
    }

    /// <summary>"userprofile/{name}" from the stored blob URL.</summary>
    private static string BlobPathOf(UserUrlDto? result)
    {
        Assert.NotNull(result?.Url);
        return $"{Container}/" + new Uri(result.Url).AbsolutePath.Split($"/{Container}/").Last();
    }

    private async Task<long> FileSizeAsync(string blobPath)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.Files.Where(f => f.Path == blobPath).Select(f => f.Size).SingleAsync(Ct);
    }

    private static byte[] Fixture(string name)
        => System.IO.File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "tests-fixtures", name));

    private static byte[] PngBytes(int width = 200, int height = 100)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }
}
