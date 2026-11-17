using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Services;
using SkiaSharp;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Print image uploads go through the same processing as printer and material images, and the
/// image endpoint serves them with a content type taken from the row, never from the file name.
/// </summary>
public class PrintImageProcessingTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private static HttpRequestMessage AuthenticatedRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        return request;
    }

    private static byte[] PngBytes(int width = 200, int height = 100)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static byte[] Fixture(string name)
        => System.IO.File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "tests-fixtures", name));

    private static MultipartFormDataContent Form(byte[] bytes, string contentType, string fileName)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(content, "image", fileName);
        return form;
    }

    private InMemoryBlobStorageService Blobs()
        => (InMemoryBlobStorageService)_factory.Services.GetRequiredService<IBlobStorageService>();

    private async Task<long> CreatePrintAsync(PrintViewStatus viewStatus = PrintViewStatus.Public)
    {
        var request = AuthenticatedRequest(HttpMethod.Post, "/api/Prints");
        request.Content = JsonContent.Create(new AddPrintDTO
        {
            Title = $"Image Processing Print {Guid.NewGuid()}",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Pending,
            ViewStatus = viewStatus,
            AllowComments = true
        });
        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var print = await response.Content.ReadFromJsonAsync<PrintDetailDTO>(TestContext.Current.CancellationToken);
        return print!.Id;
    }

    private async Task<HttpResponseMessage> UploadAsync(long printId, byte[] bytes, string contentType, string fileName)
    {
        var request = AuthenticatedRequest(HttpMethod.Post, $"/api/Prints/{printId}/image");
        request.Content = Form(bytes, contentType, fileName);
        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<PrintImage> LoadImageAsync(int imageId)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await context.PrintImages
            .Include(pi => pi.File)
            .Include(pi => pi.ThumbnailFile)
            .Include(pi => pi.SocialFile)
            .AsNoTracking()
            .SingleAsync(pi => pi.Id == imageId, TestContext.Current.CancellationToken);
    }

    /// <summary>Adds an image row as uploads stored it before processing: no content type.</summary>
    private async Task<int> SeedLegacyImageAsync(long printId, string extension, byte[] bytes)
    {
        var blobName = $"{Guid.NewGuid()}{extension}";
        using var stream = new MemoryStream(bytes);
        var upload = await Blobs().UploadAsync(BlobContainers.PrintImages, blobName, stream);

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var userId = await context.Users
            .Where(u => u.OAuthUserId == IntegrationTestSeeder.TestUserOAuthId)
            .Select(u => u.Id)
            .FirstAsync(TestContext.Current.CancellationToken);

        var file = new Models.File
        {
            Id = Guid.NewGuid(),
            Path = upload.BlobPath,
            Size = bytes.Length,
            CreatedById = userId,
            UpdatedById = userId
        };
        context.Files.Add(file);
        var image = new PrintImage
        {
            PrintId = printId,
            File = file,
            DisplayOrder = 10,
            CreatedById = userId,
            UpdatedById = userId
        };
        context.PrintImages.Add(image);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return image.Id;
    }

    private static void AssertImageResponseHeaders(HttpResponseMessage response)
    {
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("default-src 'none'; sandbox",
            Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
    }

    [Fact]
    public async Task Upload_ReEncodesAndRemovesMetadata()
    {
        var printId = await CreatePrintAsync();

        var response = await UploadAsync(printId, Fixture("exif-orientation-6.jpg"), "image/jpeg", "photo.jpg");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<PrintImageDto>(TestContext.Current.CancellationToken);
        var image = await LoadImageAsync(dto!.Id);

        Assert.Equal("image/jpeg", image.ContentType);
        // The fixture is 100x50 with orientation 6, so the stored original is upright: 50x100.
        Assert.Equal(50, image.Width);
        Assert.Equal(100, image.Height);

        var stored = Blobs().Blobs[image.File.Path!];
        Assert.DoesNotContain("Exif", System.Text.Encoding.ASCII.GetString(stored), StringComparison.Ordinal);
        using var codec = SKCodec.Create(new MemoryStream(stored));
        Assert.Equal(SKEncodedImageFormat.Jpeg, codec!.EncodedFormat);
        Assert.Equal(image.File.Size, stored.LongLength);
    }

    [Fact]
    public async Task Upload_NamesBlobWithCanonicalExtension_NotClientFileName()
    {
        var printId = await CreatePrintAsync();

        var response = await UploadAsync(printId, PngBytes(), "image/png", "picture.custom");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<PrintImageDto>(TestContext.Current.CancellationToken);
        var image = await LoadImageAsync(dto!.Id);

        Assert.StartsWith($"{BlobContainers.PrintImages}/", image.File.Path, StringComparison.Ordinal);
        Assert.EndsWith(".jpg", image.File.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_CreatesThumbnail()
    {
        var printId = await CreatePrintAsync();

        var response = await UploadAsync(printId, PngBytes(800, 400), "image/png", "big.png");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<PrintImageDto>(TestContext.Current.CancellationToken);
        var image = await LoadImageAsync(dto!.Id);

        Assert.NotNull(image.ThumbnailFile);
        Assert.EndsWith(".webp", image.ThumbnailFile!.Path, StringComparison.Ordinal);
        var thumbnail = Blobs().Blobs[image.ThumbnailFile.Path!];
        using var codec = SKCodec.Create(new MemoryStream(thumbnail));
        Assert.Equal(SKEncodedImageFormat.Webp, codec!.EncodedFormat);
        Assert.Equal(320, codec.Info.Width);
        Assert.Equal(800, image.Width);
        Assert.Equal(400, image.Height);
    }

    [Fact]
    public async Task Upload_CreatesASocialImage_WithItsSize()
    {
        var printId = await CreatePrintAsync();

        var response = await UploadAsync(printId, PngBytes(2400, 1600), "image/png", "large.png");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<PrintImageDto>(TestContext.Current.CancellationToken);
        var image = await LoadImageAsync(dto!.Id);

        Assert.NotNull(image.SocialFile);
        Assert.EndsWith(".jpg", image.SocialFile!.Path, StringComparison.Ordinal);
        Assert.NotEqual(image.File.Path, image.SocialFile.Path);
        var social = Blobs().Blobs[image.SocialFile.Path!];
        using var codec = SKCodec.Create(new MemoryStream(social));
        Assert.Equal(SKEncodedImageFormat.Jpeg, codec!.EncodedFormat);
        Assert.Equal(1200, codec.Info.Width);
        Assert.Equal(800, codec.Info.Height);
        Assert.Equal(1200, image.SocialWidth);
        Assert.Equal(800, image.SocialHeight);
        Assert.Equal(social.LongLength, image.SocialFile.Size);
    }

    [Fact]
    public async Task Upload_NonImageBytesWithImageContentType_IsRejected()
    {
        var printId = await CreatePrintAsync();
        var uploadsBefore = Blobs().UploadedBlobNames.Count;

        var response = await UploadAsync(printId, "not an image"u8.ToArray(), "image/png", "fake.png");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(uploadsBefore, Blobs().UploadedBlobNames.Count);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        Assert.False(await context.PrintImages.AnyAsync(pi => pi.PrintId == printId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Upload_KeepsDefaultAndDisplayOrderBehavior()
    {
        var printId = await CreatePrintAsync();

        var first = await (await UploadAsync(printId, PngBytes(), "image/png", "a.png"))
            .Content.ReadFromJsonAsync<PrintImageDto>(TestContext.Current.CancellationToken);
        var secondRequest = AuthenticatedRequest(HttpMethod.Post, $"/api/Prints/{printId}/image");
        var form = Form(PngBytes(), "image/png", "b.png");
        form.Add(new StringContent("true"), "isDefault");
        secondRequest.Content = form;
        var second = await (await _client.SendAsync(secondRequest, TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<PrintImageDto>(TestContext.Current.CancellationToken);

        Assert.Equal(0, first!.DisplayOrder);
        Assert.Equal(1, second!.DisplayOrder);
        Assert.True(second.IsDefault);
        Assert.False((await LoadImageAsync(first.Id)).IsDefault);
    }

    [Fact]
    public async Task GetImage_ServesStoredContentType_WithImageHeaders()
    {
        var printId = await CreatePrintAsync();
        var dto = await (await UploadAsync(printId, PngBytes(), "image/png", "served.png"))
            .Content.ReadFromJsonAsync<PrintImageDto>(TestContext.Current.CancellationToken);

        var response = await _client.GetAsync($"/api/Prints/{printId}/image/{dto!.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Content.Headers.ContentDisposition);
        AssertImageResponseHeaders(response);
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".svg")]
    [InlineData(".pdf")]
    [InlineData("")]
    public async Task GetImage_LegacyRowWithNonImageExtension_IsServedAsAttachment(string extension)
    {
        var printId = await CreatePrintAsync();
        var imageId = await SeedLegacyImageAsync(printId, extension, "legacy bytes"u8.ToArray());

        var response = await _client.GetAsync($"/api/Prints/{printId}/image/{imageId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        AssertImageResponseHeaders(response);
    }

    [Theory]
    [InlineData(".png", "image/png")]
    [InlineData(".JPG", "image/jpeg")]
    [InlineData(".gif", "image/gif")]
    [InlineData(".webp", "image/webp")]
    [InlineData(".bmp", "image/bmp")]
    public async Task GetImage_LegacyRowWithRasterExtension_IsServedInline(string extension, string expected)
    {
        var printId = await CreatePrintAsync();
        var imageId = await SeedLegacyImageAsync(printId, extension, PngBytes());

        var response = await _client.GetAsync($"/api/Prints/{printId}/image/{imageId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Content.Headers.ContentDisposition);
        AssertImageResponseHeaders(response);
    }

    [Fact]
    public async Task GetImage_PrivatePrint_IsStillForbiddenAnonymously()
    {
        var printId = await CreatePrintAsync(PrintViewStatus.Private);
        var dto = await (await UploadAsync(printId, PngBytes(), "image/png", "private.png"))
            .Content.ReadFromJsonAsync<PrintImageDto>(TestContext.Current.CancellationToken);

        var anonymous = await _client.GetAsync($"/api/Prints/{printId}/image/{dto!.Id}", TestContext.Current.CancellationToken);
        var owner = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, $"/api/Prints/{printId}/image/{dto.Id}"),
            TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.OK, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
    }

    [Fact]
    public async Task RemoveImage_DeletesOriginalAndDerivatives()
    {
        var printId = await CreatePrintAsync();
        var dto = await (await UploadAsync(printId, PngBytes(), "image/png", "remove.png"))
            .Content.ReadFromJsonAsync<PrintImageDto>(TestContext.Current.CancellationToken);
        var image = await LoadImageAsync(dto!.Id);
        Assert.NotNull(image.ThumbnailFileId);
        Assert.NotNull(image.SocialFileId);

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Delete, $"/api/Prints/{printId}/image/{dto.Id}"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(Blobs().BlobExists(image.File.Path!));
        Assert.False(Blobs().BlobExists(image.ThumbnailFile!.Path!));
        Assert.False(Blobs().BlobExists(image.SocialFile!.Path!));
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        Assert.False(await context.Files.AnyAsync(
            f => f.Id == image.FileId || f.Id == image.ThumbnailFileId || f.Id == image.SocialFileId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeletePrint_RemovesDerivativeFileRows()
    {
        var printId = await CreatePrintAsync();
        var dto = await (await UploadAsync(printId, PngBytes(), "image/png", "print-delete.png"))
            .Content.ReadFromJsonAsync<PrintImageDto>(TestContext.Current.CancellationToken);
        var image = await LoadImageAsync(dto!.Id);

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Delete, $"/api/Prints/{printId}"),
            TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        Assert.NotNull(image.SocialFileId);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        Assert.False(await context.Files.AnyAsync(
            f => f.Id == image.FileId || f.Id == image.ThumbnailFileId || f.Id == image.SocialFileId,
            TestContext.Current.CancellationToken));
    }
}
