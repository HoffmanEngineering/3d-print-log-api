using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Project;
using PrintLogApi.Services;
using SkiaSharp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Project image uploads go through the same processing as printer and material images, and the
/// image endpoint serves them with a content type taken from the row, never from the file name.
/// </summary>
public class ProjectImageProcessingTests(CustomWebApplicationFactory factory)
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

    private InMemoryBlobStorageService Blobs()
        => (InMemoryBlobStorageService)_factory.Services.GetRequiredService<IBlobStorageService>();

    private async Task<Guid> CreateProjectAsync(Project.ProjectViewStatus viewStatus = Project.ProjectViewStatus.Public)
    {
        var request = AuthenticatedRequest(HttpMethod.Post, "/api/Projects");
        request.Content = JsonContent.Create(new AddProjectDto
        {
            Name = $"Image Processing Project {Guid.NewGuid()}",
            Status = Project.ProjectStatus.InProgress,
            ViewStatus = viewStatus
        });
        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var project = await response.Content.ReadFromJsonAsync<ProjectDetailDto>(TestContext.Current.CancellationToken);
        return project!.Id;
    }

    private async Task<HttpResponseMessage> UploadAsync(Guid projectId, byte[] bytes, string contentType, string fileName)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(content, "file", fileName);
        var request = AuthenticatedRequest(HttpMethod.Post, $"/api/Projects/{projectId}/images");
        request.Content = form;
        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<int> UploadPngAsync(Guid projectId)
    {
        var response = await UploadAsync(projectId, PngBytes(), "image/png", "project.png");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ProjectImageDto>(TestContext.Current.CancellationToken);
        return dto!.Id;
    }

    private async Task<ProjectImage> LoadImageAsync(int imageId)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await context.ProjectImages
            .Include(pi => pi.File)
            .Include(pi => pi.ThumbnailFile)
            .Include(pi => pi.SocialFile)
            .AsNoTracking()
            .SingleAsync(pi => pi.Id == imageId, TestContext.Current.CancellationToken);
    }

    /// <summary>Adds an image row as uploads stored it before processing: bare blob name, no type.</summary>
    private async Task<int> SeedLegacyImageAsync(Guid projectId, string extension, byte[] bytes)
    {
        var blobName = $"{Guid.NewGuid()}{extension}";
        using var stream = new MemoryStream(bytes);
        await Blobs().UploadAsync(BlobContainers.ProjectImages, blobName, stream);

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var userId = await context.Users
            .Where(u => u.OAuthUserId == IntegrationTestSeeder.TestUserOAuthId)
            .Select(u => u.Id)
            .FirstAsync(TestContext.Current.CancellationToken);

        var file = new Models.File { Path = blobName, Size = bytes.Length, CreatedById = userId, UpdatedById = userId };
        context.Files.Add(file);
        var image = new ProjectImage
        {
            ProjectId = projectId,
            File = file,
            DisplayOrder = 10,
            CreatedById = userId,
            UpdatedById = userId
        };
        context.ProjectImages.Add(image);
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
        var projectId = await CreateProjectAsync();

        var response = await UploadAsync(projectId, Fixture("exif-orientation-6.jpg"), "image/jpeg", "photo.jpg");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ProjectImageDto>(TestContext.Current.CancellationToken);
        var image = await LoadImageAsync(dto!.Id);

        Assert.Equal("image/jpeg", image.ContentType);
        Assert.Equal(50, image.Width);
        Assert.Equal(100, image.Height);
        var stored = Blobs().Blobs[image.File.Path!];
        Assert.DoesNotContain("Exif", System.Text.Encoding.ASCII.GetString(stored), StringComparison.Ordinal);
        Assert.Equal(image.File.Size, stored.LongLength);
    }

    [Fact]
    public async Task Upload_NamesBlobsWithCanonicalExtensions_AndCreatesThumbnail()
    {
        var projectId = await CreateProjectAsync();

        var response = await UploadAsync(projectId, PngBytes(), "image/png", "picture.custom");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ProjectImageDto>(TestContext.Current.CancellationToken);
        var image = await LoadImageAsync(dto!.Id);

        Assert.StartsWith($"{BlobContainers.ProjectImages}/", image.File.Path, StringComparison.Ordinal);
        Assert.EndsWith(".jpg", image.File.Path, StringComparison.Ordinal);
        Assert.NotNull(image.ThumbnailFile);
        Assert.EndsWith(".webp", image.ThumbnailFile!.Path, StringComparison.Ordinal);
        Assert.True(Blobs().BlobExists(image.ThumbnailFile.Path!));
        Assert.NotNull(image.SocialFile);
        Assert.EndsWith(".jpg", image.SocialFile!.Path, StringComparison.Ordinal);
        Assert.True(Blobs().BlobExists(image.SocialFile.Path!));
        Assert.Equal(image.Width, image.SocialWidth); // small upload: not upscaled
        Assert.Equal(image.Height, image.SocialHeight);
    }

    [Fact]
    public async Task Upload_NonImageBytesWithImageContentType_IsRejected()
    {
        var projectId = await CreateProjectAsync();
        var uploadsBefore = Blobs().UploadedBlobNames.Count;

        var response = await UploadAsync(projectId, "not an image"u8.ToArray(), "image/png", "fake.png");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(uploadsBefore, Blobs().UploadedBlobNames.Count);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        Assert.False(await context.ProjectImages.AnyAsync(pi => pi.ProjectId == projectId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetImage_ServesStoredContentType_WithImageHeaders()
    {
        var projectId = await CreateProjectAsync();
        var imageId = await UploadPngAsync(projectId);

        var response = await _client.GetAsync($"/api/Projects/{projectId}/images/{imageId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Content.Headers.ContentDisposition);
        AssertImageResponseHeaders(response);
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".svg")]
    [InlineData("")]
    public async Task GetImage_LegacyRowWithNonImageExtension_IsServedAsAttachment(string extension)
    {
        var projectId = await CreateProjectAsync();
        var imageId = await SeedLegacyImageAsync(projectId, extension, "legacy bytes"u8.ToArray());

        var response = await _client.GetAsync($"/api/Projects/{projectId}/images/{imageId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        AssertImageResponseHeaders(response);
    }

    [Fact]
    public async Task GetImage_LegacyRowWithRasterExtension_IsServedInline()
    {
        var projectId = await CreateProjectAsync();
        var imageId = await SeedLegacyImageAsync(projectId, ".png", PngBytes());

        var response = await _client.GetAsync($"/api/Projects/{projectId}/images/{imageId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        AssertImageResponseHeaders(response);
    }

    [Fact]
    public async Task GetImage_PrivateProject_IsStillHiddenAnonymously()
    {
        var projectId = await CreateProjectAsync(Project.ProjectViewStatus.Private);
        var imageId = await UploadPngAsync(projectId);

        var anonymous = await _client.GetAsync($"/api/Projects/{projectId}/images/{imageId}", TestContext.Current.CancellationToken);
        var owner = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, $"/api/Projects/{projectId}/images/{imageId}"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
    }

    [Fact]
    public async Task DeleteImage_DeletesOriginalAndThumbnail()
    {
        var projectId = await CreateProjectAsync();
        var image = await LoadImageAsync(await UploadPngAsync(projectId));

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Delete, $"/api/Projects/{projectId}/images/{image.Id}"),
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
    public async Task DeleteProject_DeletesOriginalAndThumbnail()
    {
        var projectId = await CreateProjectAsync();
        var image = await LoadImageAsync(await UploadPngAsync(projectId));

        var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Delete, $"/api/Projects/{projectId}"),
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
}
