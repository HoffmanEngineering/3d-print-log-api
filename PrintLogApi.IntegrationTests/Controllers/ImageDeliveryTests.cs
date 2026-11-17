using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Project;
using PrintLogApi.Services;
using SkiaSharp;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// How print and project images are delivered: streamed with an ETag, conditional and range
/// requests, shared-cacheable only when the print or project is not private, and the thumbnail
/// and social-image variants with their fallbacks.
/// </summary>
public class ImageDeliveryTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpRequestMessage AuthenticatedRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        return request;
    }

    private static byte[] PngBytes(int width = 1600, int height = 1000)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private InMemoryBlobStorageService Blobs()
        => (InMemoryBlobStorageService)_factory.Services.GetRequiredService<IBlobStorageService>();

    private async Task<long> CreatePrintAsync(PrintViewStatus viewStatus)
    {
        var request = AuthenticatedRequest(HttpMethod.Post, "/api/Prints");
        request.Content = JsonContent.Create(new AddPrintDTO
        {
            Title = $"Delivery Print {Guid.NewGuid()}",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Success,
            ViewStatus = viewStatus,
        });
        var response = await _client.SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PrintDetailDTO>(Ct))!.Id;
    }

    private async Task<int> UploadPrintImageAsync(long printId, bool isDefault = false)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(PngBytes());
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(content, "image", "photo.png");
        if (isDefault) form.Add(new StringContent("true"), "isDefault");
        var request = AuthenticatedRequest(HttpMethod.Post, $"/api/Prints/{printId}/image");
        request.Content = form;
        var response = await _client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PrintImageDto>(Ct))!.Id;
    }

    private async Task<Guid> CreateProjectAsync(Project.ProjectViewStatus viewStatus)
    {
        var request = AuthenticatedRequest(HttpMethod.Post, "/api/Projects");
        request.Content = JsonContent.Create(new AddProjectDto
        {
            Name = $"Delivery Project {Guid.NewGuid()}",
            Status = Project.ProjectStatus.InProgress,
            ViewStatus = viewStatus,
        });
        var response = await _client.SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectDetailDto>(Ct))!.Id;
    }

    private async Task<int> UploadProjectImageAsync(Guid projectId)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(PngBytes());
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(content, "file", "project.png");
        var request = AuthenticatedRequest(HttpMethod.Post, $"/api/Projects/{projectId}/images");
        request.Content = form;
        var response = await _client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProjectImageDto>(Ct))!.Id;
    }

    /// <summary>Clears a derivative from a row, as on an image uploaded before it existed.</summary>
    private async Task ClearDerivativesAsync(int printImageId, bool thumbnail, bool social)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var row = await context.PrintImages.SingleAsync(i => i.Id == printImageId, Ct);
        if (thumbnail) row.ThumbnailFileId = null;
        if (social)
        {
            row.SocialFileId = null;
            row.SocialWidth = null;
            row.SocialHeight = null;
        }
        await context.SaveChangesAsync(Ct);
    }

    private async Task<PrintImage> LoadPrintImageAsync(int imageId)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await context.PrintImages
            .Include(i => i.File).Include(i => i.ThumbnailFile).Include(i => i.SocialFile)
            .AsNoTracking()
            .SingleAsync(i => i.Id == imageId, Ct);
    }

    private static void AssertImageHeaders(HttpResponseMessage response)
    {
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("default-src 'none'; sandbox",
            Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
    }

    private static void AssertSharedCacheable(HttpResponseMessage response)
    {
        var cache = response.Headers.CacheControl;
        Assert.NotNull(cache);
        Assert.True(cache!.Public, $"expected public, got {cache}");
        Assert.False(cache.Private);
        Assert.Equal(TimeSpan.FromHours(1), cache.MaxAge);
    }

    private static void AssertNotSharedCacheable(HttpResponseMessage response)
    {
        var cache = response.Headers.CacheControl;
        Assert.NotNull(cache);
        Assert.True(cache!.Private, $"expected private, got {cache}");
        Assert.False(cache.Public);
    }

    // ---- Original ----

    [Fact]
    public async Task PublicPrintImage_IsSharedCacheableForAnHour_WithAnETag()
    {
        var printId = await CreatePrintAsync(PrintViewStatus.Public);
        var imageId = await UploadPrintImageAsync(printId);

        var response = await _client.GetAsync($"/api/Prints/{printId}/image/{imageId}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        AssertSharedCacheable(response);
        Assert.NotNull(response.Headers.ETag);
        Assert.False(response.Headers.ETag!.IsWeak);
        AssertImageHeaders(response);
        // Streamed forward-only, so the length and range support are stated by the endpoint.
        Assert.Contains("bytes", response.Headers.AcceptRanges);
        var image = await LoadPrintImageAsync(imageId);
        var stored = Blobs().Blobs[image.File.Path!];
        Assert.Equal(stored.LongLength, response.Content.Headers.ContentLength);
        Assert.Equal(stored, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task UnlistedPrintImage_IsSharedCacheable_LikeAnyImageAnonymousCallersCanRead()
    {
        var printId = await CreatePrintAsync(PrintViewStatus.Unlisted);
        var imageId = await UploadPrintImageAsync(printId);

        var response = await _client.GetAsync($"/api/Prints/{printId}/image/{imageId}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertSharedCacheable(response);
    }

    [Fact]
    public async Task PrivatePrintImage_IsNeverSharedCacheable()
    {
        var printId = await CreatePrintAsync(PrintViewStatus.Private);
        var imageId = await UploadPrintImageAsync(printId);

        var owner = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, $"/api/Prints/{printId}/image/{imageId}"), Ct);
        var anonymous = await _client.GetAsync($"/api/Prints/{printId}/image/{imageId}", Ct);

        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        AssertNotSharedCacheable(owner);
        AssertImageHeaders(owner);
        Assert.Equal(HttpStatusCode.Forbidden, anonymous.StatusCode);
        Assert.False(anonymous.Headers.CacheControl?.Public ?? false);
    }

    [Fact]
    public async Task MatchingIfNoneMatch_Returns304_WithoutReadingTheBlob()
    {
        var printId = await CreatePrintAsync(PrintViewStatus.Public);
        var imageId = await UploadPrintImageAsync(printId);
        var url = $"/api/Prints/{printId}/image/{imageId}";
        var first = await _client.GetAsync(url, Ct);
        var etag = first.Headers.ETag!;
        var readsBefore = Blobs().OpenReadCount;

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.IfNoneMatch.Add(etag);
        var response = await _client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Equal(etag, response.Headers.ETag);
        AssertSharedCacheable(response);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(readsBefore, Blobs().OpenReadCount);
    }

    [Fact]
    public async Task StaleIfNoneMatch_ReturnsTheImage()
    {
        var printId = await CreatePrintAsync(PrintViewStatus.Public);
        var imageId = await UploadPrintImageAsync(printId);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Prints/{printId}/image/{imageId}");
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"something-else\""));
        var response = await _client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ETag_ChangesWhenTheBlobDoes()
    {
        // The backfill points an existing File row at a new blob, so a tag from the File id alone
        // would let a cache keep the old bytes. The tag follows the blob.
        var printId = await CreatePrintAsync(PrintViewStatus.Public);
        var imageId = await UploadPrintImageAsync(printId);
        var url = $"/api/Prints/{printId}/image/{imageId}";
        var before = (await _client.GetAsync(url, Ct)).Headers.ETag;

        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var row = await context.PrintImages.Include(i => i.File).SingleAsync(i => i.Id == imageId, Ct);
            var newName = $"{Guid.NewGuid()}.jpg";
            using var stream = new MemoryStream(Blobs().Blobs[row.File.Path!]);
            row.File.Path = (await Blobs().UploadAsync(BlobContainers.PrintImages, newName, stream)).BlobPath;
            await context.SaveChangesAsync(Ct);
        }
        var after = (await _client.GetAsync(url, Ct)).Headers.ETag;

        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task RangeRequest_ReturnsPartialContent()
    {
        var printId = await CreatePrintAsync(PrintViewStatus.Public);
        var imageId = await UploadPrintImageAsync(printId);
        var bytes = Blobs().Blobs[(await LoadPrintImageAsync(imageId)).File.Path!];

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Prints/{printId}/image/{imageId}");
        request.Headers.Range = new RangeHeaderValue(0, 9);
        var response = await _client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(bytes.Take(10).ToArray(), await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(bytes.LongLength, response.Content.Headers.ContentRange?.Length);
        Assert.Contains("bytes", response.Headers.AcceptRanges);
        AssertImageHeaders(response);
    }

    [Fact]
    public async Task MissingBlob_Is404_AndNotCacheable()
    {
        var printId = await CreatePrintAsync(PrintViewStatus.Public);
        var imageId = await UploadPrintImageAsync(printId);
        Blobs().Blobs.Remove((await LoadPrintImageAsync(imageId)).File.Path!);

        var response = await _client.GetAsync($"/api/Prints/{printId}/image/{imageId}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.CacheControl?.Public ?? false);
        Assert.Null(response.Headers.ETag);
    }

    // ---- Thumbnail ----

    [Fact]
    public async Task PrintThumbnail_ServesTheWebpDerivative()
    {
        var printId = await CreatePrintAsync(PrintViewStatus.Public);
        var imageId = await UploadPrintImageAsync(printId);
        var image = await LoadPrintImageAsync(imageId);

        var response = await _client.GetAsync($"/api/Prints/{printId}/image/{imageId}/thumbnail", Ct);
        var original = await _client.GetAsync($"/api/Prints/{printId}/image/{imageId}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Blobs().Blobs[image.ThumbnailFile!.Path!], await response.Content.ReadAsByteArrayAsync(Ct));
        AssertSharedCacheable(response);
        AssertImageHeaders(response);
        Assert.NotEqual(original.Headers.ETag, response.Headers.ETag);
    }

    [Fact]
    public async Task PrintThumbnail_FallsBackToTheOriginal_WhenThereIsNone()
    {
        var printId = await CreatePrintAsync(PrintViewStatus.Public);
        var imageId = await UploadPrintImageAsync(printId);
        await ClearDerivativesAsync(imageId, thumbnail: true, social: false);
        var image = await LoadPrintImageAsync(imageId);

        var response = await _client.GetAsync($"/api/Prints/{printId}/image/{imageId}/thumbnail", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Blobs().Blobs[image.File.Path!], await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task PrintThumbnail_OfAPrivatePrint_HasTheOriginalsAuthorization()
    {
        var printId = await CreatePrintAsync(PrintViewStatus.Private);
        var imageId = await UploadPrintImageAsync(printId);
        var url = $"/api/Prints/{printId}/image/{imageId}/thumbnail";

        var anonymous = await _client.GetAsync(url, Ct);
        var owner = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, url), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        AssertNotSharedCacheable(owner);
    }

    [Fact]
    public async Task PrintThumbnail_UnknownImage_Is404()
    {
        var printId = await CreatePrintAsync(PrintViewStatus.Public);

        var response = await _client.GetAsync($"/api/Prints/{printId}/image/999999/thumbnail", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ProjectThumbnail_ServesTheDerivative_AndHidesPrivateProjects()
    {
        var publicId = await CreateProjectAsync(Project.ProjectViewStatus.Public);
        var publicImage = await UploadProjectImageAsync(publicId);
        var privateId = await CreateProjectAsync(Project.ProjectViewStatus.Private);
        var privateImage = await UploadProjectImageAsync(privateId);

        var response = await _client.GetAsync($"/api/Projects/{publicId}/images/{publicImage}/thumbnail", Ct);
        var hidden = await _client.GetAsync($"/api/Projects/{privateId}/images/{privateImage}/thumbnail", Ct);
        var owner = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, $"/api/Projects/{privateId}/images/{privateImage}/thumbnail"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
        AssertSharedCacheable(response);
        AssertImageHeaders(response);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        AssertNotSharedCacheable(owner);
    }

    [Fact]
    public async Task ProjectImage_IsSharedCacheable_WithAnETag_And304()
    {
        var projectId = await CreateProjectAsync(Project.ProjectViewStatus.Public);
        var imageId = await UploadProjectImageAsync(projectId);
        var url = $"/api/Projects/{projectId}/images/{imageId}";

        var first = await _client.GetAsync(url, Ct);
        var conditional = new HttpRequestMessage(HttpMethod.Get, url);
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        var second = await _client.SendAsync(conditional, Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        AssertSharedCacheable(first);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    // ---- Social image ----

    [Fact]
    public async Task PrintSocialImage_ServesTheDefaultImagesSocialJpeg()
    {
        var printId = await CreatePrintAsync(PrintViewStatus.Public);
        await UploadPrintImageAsync(printId);
        var defaultId = await UploadPrintImageAsync(printId, isDefault: true);
        var image = await LoadPrintImageAsync(defaultId);

        var response = await _client.GetAsync($"/api/Prints/{printId}/social-image?v=anything", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Blobs().Blobs[image.SocialFile!.Path!], await response.Content.ReadAsByteArrayAsync(Ct));
        AssertSharedCacheable(response);
        AssertImageHeaders(response);
    }

    [Fact]
    public async Task PrintSocialImage_FallsBackToTheOriginal_WhenThereIsNone()
    {
        var printId = await CreatePrintAsync(PrintViewStatus.Public);
        var imageId = await UploadPrintImageAsync(printId);
        await ClearDerivativesAsync(imageId, thumbnail: false, social: true);
        var image = await LoadPrintImageAsync(imageId);

        var response = await _client.GetAsync($"/api/Prints/{printId}/social-image", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Blobs().Blobs[image.File.Path!], await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Theory]
    [InlineData(PrintViewStatus.Private)]
    [InlineData(PrintViewStatus.Unlisted)]
    public async Task PrintSocialImage_OfAPrintThatIsNotPublic_Is404_EvenForTheOwner(PrintViewStatus viewStatus)
    {
        var printId = await CreatePrintAsync(viewStatus);
        await UploadPrintImageAsync(printId);

        var anonymous = await _client.GetAsync($"/api/Prints/{printId}/social-image", Ct);
        var owner = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, $"/api/Prints/{printId}/social-image"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, owner.StatusCode);
    }

    [Fact]
    public async Task PrintSocialImage_WithoutAnyImage_OrForAnUnknownPrint_Is404()
    {
        var printId = await CreatePrintAsync(PrintViewStatus.Public);

        var noImage = await _client.GetAsync($"/api/Prints/{printId}/social-image", Ct);
        var unknown = await _client.GetAsync("/api/Prints/987654321/social-image", Ct);

        Assert.Equal(HttpStatusCode.NotFound, noImage.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task ProjectSocialImage_ServesThePublicProjectsDefaultImage_And404sOtherwise()
    {
        var publicId = await CreateProjectAsync(Project.ProjectViewStatus.Public);
        await UploadProjectImageAsync(publicId);
        var unlistedId = await CreateProjectAsync(Project.ProjectViewStatus.Unlisted);
        await UploadProjectImageAsync(unlistedId);
        var emptyId = await CreateProjectAsync(Project.ProjectViewStatus.Public);

        var response = await _client.GetAsync($"/api/Projects/{publicId}/social-image", Ct);
        var unlisted = await _client.GetAsync($"/api/Projects/{unlistedId}/social-image", Ct);
        var empty = await _client.GetAsync($"/api/Projects/{emptyId}/social-image", Ct);
        var unknown = await _client.GetAsync($"/api/Projects/{Guid.NewGuid()}/social-image", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        AssertSharedCacheable(response);
        AssertImageHeaders(response);
        Assert.Equal(HttpStatusCode.NotFound, unlisted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, empty.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
