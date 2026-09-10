using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Printer;
using PrintLogApi.Services;
using SkiaSharp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

public class PrinterImagesControllerTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private const string ForeignUserOAuthId = "auth0|printer-image-foreign-user";

    private static HttpRequestMessage Request(HttpMethod method, string url, string oauthId)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, oauthId);
        return request;
    }

    private static HttpRequestMessage AuthenticatedRequest(HttpMethod method, string url)
        => Request(method, url, IntegrationTestSeeder.TestUserOAuthId);

    private static byte[] PngBytes(int width = 200, int height = 100)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static MultipartFormDataContent Form(byte[] bytes, string contentType, string fileName)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(content, "file", fileName);
        return form;
    }

    private static MultipartFormDataContent ValidMultipart()
        => Form(PngBytes(), "image/png", "printer.png");

    private T Resolve<T>() where T : notnull
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<T>();
    }

    private InMemoryBlobStorageService Blobs()
        => (InMemoryBlobStorageService)_factory.Services.GetRequiredService<IBlobStorageService>();

    /// <summary>
    /// Creates a printer owned by <paramref name="oauthId"/>, adding that user first if this
    /// database does not have them yet.
    /// </summary>
    private async Task<long> CreatePrinterAsync(CancellationToken ct, string? oauthId = null)
    {
        oauthId ??= IntegrationTestSeeder.TestUserOAuthId;

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var userId = await context.Users
            .Where(u => u.OAuthUserId == oauthId)
            .Select(u => (long?)u.Id)
            .FirstOrDefaultAsync(ct);

        if (userId is null)
        {
            var user = new User { OAuthUserId = oauthId, DisplayName = "Foreign User" };
            context.Users.Add(user);
            await context.SaveChangesAsync(ct);
            userId = user.Id;
        }

        var printer = new Printer
        {
            Name = $"Endpoint Printer {Guid.NewGuid()}",
            Make = "Prusa",
            Model = "MK4",
            UserId = userId.Value,
            IsActive = true
        };
        context.Printers.Add(printer);
        await context.SaveChangesAsync(ct);
        return printer.Id;
    }

    private async Task<PrinterImageDto> UploadAsync(long printerId, CancellationToken ct)
    {
        var req = AuthenticatedRequest(HttpMethod.Post, $"/api/Printers/{printerId}/images");
        req.Content = ValidMultipart();

        var resp = await _client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<PrinterImageDto>(ct))!;
    }

    // -- Upload ------------------------------------------------------------------------

    [Fact]
    public async Task PostImage_Valid_Returns201WithSignedUrl()
    {
        var ct = TestContext.Current.CancellationToken;
        var printerId = await CreatePrinterAsync(ct);

        var req = AuthenticatedRequest(HttpMethod.Post, $"/api/Printers/{printerId}/images");
        req.Content = ValidMultipart();

        var resp = await _client.SendAsync(req, ct);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var dto = await resp.Content.ReadFromJsonAsync<PrinterImageDto>(ct);
        Assert.NotNull(dto);
        Assert.True(dto!.Id > 0);
        Assert.False(string.IsNullOrEmpty(dto.Url));
        Assert.False(string.IsNullOrEmpty(dto.ThumbnailUrl));
        Assert.True(dto.IsDefault); // first image
    }

    [Fact]
    public async Task PostImage_TextFileLabeledPng_Returns400()
    {
        // The declared content type from the client is not trusted; decoding is the validation.
        var ct = TestContext.Current.CancellationToken;
        var printerId = await CreatePrinterAsync(ct);

        var req = AuthenticatedRequest(HttpMethod.Post, $"/api/Printers/{printerId}/images");
        req.Content = Form("not an image at all"u8.ToArray(), "image/png", "printer.png");

        var resp = await _client.SendAsync(req, ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostImage_ForeignPrinter_Returns404()
    {
        // 404 rather than 403: the endpoint must not confirm that the printer exists.
        var ct = TestContext.Current.CancellationToken;
        var foreignPrinterId = await CreatePrinterAsync(ct, ForeignUserOAuthId);

        var req = AuthenticatedRequest(HttpMethod.Post, $"/api/Printers/{foreignPrinterId}/images");
        req.Content = ValidMultipart();

        var resp = await _client.SendAsync(req, ct);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task PostImage_NoFile_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var printerId = await CreatePrinterAsync(ct);

        var req = AuthenticatedRequest(HttpMethod.Post, $"/api/Printers/{printerId}/images");
        req.Content = new MultipartFormDataContent();

        var resp = await _client.SendAsync(req, ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostImage_PastCap_Returns400NotAServerError()
    {
        // This app has no global exception-to-status mapping, so an uncaught quota
        // exception is a 500, not a 400.
        var ct = TestContext.Current.CancellationToken;
        var printerId = await CreatePrinterAsync(ct);

        for (var i = 0; i < SubscriptionLimits.FreeMaxImages; i++)
            await UploadAsync(printerId, ct);

        var req = AuthenticatedRequest(HttpMethod.Post, $"/api/Printers/{printerId}/images");
        req.Content = ValidMultipart();

        var resp = await _client.SendAsync(req, ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostImage_BumpsUserCacheVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        var printerId = await CreatePrinterAsync(ct);

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var cacheVersion = scope.ServiceProvider.GetRequiredService<ICacheVersionService>();
        var userId = await context.Users
            .Where(u => u.OAuthUserId == IntegrationTestSeeder.TestUserOAuthId)
            .Select(u => u.Id).FirstAsync(ct);

        var before = cacheVersion.GetUserCacheVersion(userId);
        await UploadAsync(printerId, ct);

        Assert.NotEqual(before, cacheVersion.GetUserCacheVersion(userId));
    }

    // -- Read --------------------------------------------------------------------------

    [Fact]
    public async Task GetImage_Returns302WithSignedLocation()
    {
        var ct = TestContext.Current.CancellationToken;
        var printerId = await CreatePrinterAsync(ct);
        var image = await UploadAsync(printerId, ct);

        // The default HttpClient follows redirects, which would turn this into a request
        // against a fake blob host. Assert on the 302 itself.
        using var noRedirect = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });

        var resp = await noRedirect.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, $"/api/Printers/{printerId}/images/{image.Id}"), ct);

        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        Assert.NotNull(resp.Headers.Location);
        Assert.Contains(BlobContainers.PrinterImages, resp.Headers.Location!.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetImage_ForForeignPrinter_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var printerId = await CreatePrinterAsync(ct);
        var image = await UploadAsync(printerId, ct);

        // Same image id, different caller. Ownership is part of the lookup predicate.
        await CreatePrinterAsync(ct, ForeignUserOAuthId);

        var resp = await _client.SendAsync(
            Request(HttpMethod.Get, $"/api/Printers/{printerId}/images/{image.Id}", ForeignUserOAuthId), ct);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetPrinter_ReturnsHydratedImages()
    {
        var ct = TestContext.Current.CancellationToken;
        var printerId = await CreatePrinterAsync(ct);
        await UploadAsync(printerId, ct);

        var resp = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, $"/api/Printers/{printerId}"), ct);
        resp.EnsureSuccessStatusCode();

        var dto = await resp.Content.ReadFromJsonAsync<PrinterDetailDto>(ct);

        Assert.NotNull(dto!.Images);
        var single = Assert.Single(dto.Images!);
        Assert.Contains("sig=", single.Url);
        Assert.True(single.IsDefault);
    }

    [Fact]
    public async Task GetPrinter_WithNoImages_ReturnsAnEmptyListNotNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var printerId = await CreatePrinterAsync(ct);

        var resp = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, $"/api/Printers/{printerId}"), ct);
        resp.EnsureSuccessStatusCode();

        var dto = await resp.Content.ReadFromJsonAsync<PrinterDetailDto>(ct);

        Assert.NotNull(dto!.Images);
        Assert.Empty(dto.Images!);
    }

    // -- Mutations ---------------------------------------------------------------------

    [Fact]
    public async Task DeleteImage_Returns204AndBumpsCacheVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        var printerId = await CreatePrinterAsync(ct);
        var image = await UploadAsync(printerId, ct);

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var cacheVersion = scope.ServiceProvider.GetRequiredService<ICacheVersionService>();
        var userId = await context.Users
            .Where(u => u.OAuthUserId == IntegrationTestSeeder.TestUserOAuthId)
            .Select(u => u.Id).FirstAsync(ct);
        var before = cacheVersion.GetUserCacheVersion(userId);

        var resp = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Delete, $"/api/Printers/{printerId}/images/{image.Id}"), ct);

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.NotEqual(before, cacheVersion.GetUserCacheVersion(userId));
    }

    [Fact]
    public async Task DeleteImage_ForeignPrinter_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var foreignPrinterId = await CreatePrinterAsync(ct, ForeignUserOAuthId);

        var resp = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Delete, $"/api/Printers/{foreignPrinterId}/images/1"), ct);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Reorder_PartialIdSet_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var printerId = await CreatePrinterAsync(ct);
        var first = await UploadAsync(printerId, ct);
        await UploadAsync(printerId, ct);

        var req = AuthenticatedRequest(HttpMethod.Put, $"/api/Printers/{printerId}/images/reorder");
        req.Content = JsonContent.Create(new[] { first.Id });

        var resp = await _client.SendAsync(req, ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Reorder_CompleteSet_Returns204AndBumpsCacheVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        var printerId = await CreatePrinterAsync(ct);
        var first = await UploadAsync(printerId, ct);
        var second = await UploadAsync(printerId, ct);

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var cacheVersion = scope.ServiceProvider.GetRequiredService<ICacheVersionService>();
        var userId = await context.Users
            .Where(u => u.OAuthUserId == IntegrationTestSeeder.TestUserOAuthId)
            .Select(u => u.Id).FirstAsync(ct);
        var before = cacheVersion.GetUserCacheVersion(userId);

        var req = AuthenticatedRequest(HttpMethod.Put, $"/api/Printers/{printerId}/images/reorder");
        req.Content = JsonContent.Create(new[] { second.Id, first.Id });

        var resp = await _client.SendAsync(req, ct);

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.NotEqual(before, cacheVersion.GetUserCacheVersion(userId));
    }

    [Fact]
    public async Task Reorder_ForeignPrinter_Returns400()
    {
        // The owner filter empties the candidate set, so the exact-set check rejects it
        // before anything is written. Either way the caller learns nothing about the printer.
        var ct = TestContext.Current.CancellationToken;
        var foreignPrinterId = await CreatePrinterAsync(ct, ForeignUserOAuthId);

        var req = AuthenticatedRequest(HttpMethod.Put, $"/api/Printers/{foreignPrinterId}/images/reorder");
        req.Content = JsonContent.Create(new[] { 1 });

        var resp = await _client.SendAsync(req, ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task SetAsDefault_MovesTheDefaultAndBumpsCacheVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        var printerId = await CreatePrinterAsync(ct);
        var first = await UploadAsync(printerId, ct);
        var second = await UploadAsync(printerId, ct);

        Assert.True(first.IsDefault);
        Assert.False(second.IsDefault);

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var cacheVersion = scope.ServiceProvider.GetRequiredService<ICacheVersionService>();
        var userId = await context.Users
            .Where(u => u.OAuthUserId == IntegrationTestSeeder.TestUserOAuthId)
            .Select(u => u.Id).FirstAsync(ct);
        var before = cacheVersion.GetUserCacheVersion(userId);

        var resp = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Post,
                $"/api/Printers/{printerId}/images/{second.Id}/set-as-default"), ct);

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.NotEqual(before, cacheVersion.GetUserCacheVersion(userId));

        var images = await context.PrinterImages
            .AsNoTracking()
            .Where(pi => pi.PrinterId == printerId)
            .ToListAsync(ct);

        Assert.Single(images, i => i.IsDefault);
        Assert.True(images.Single(i => i.Id == second.Id).IsDefault);
    }

    [Fact]
    public async Task SetAsDefault_ForeignPrinter_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var foreignPrinterId = await CreatePrinterAsync(ct, ForeignUserOAuthId);

        var resp = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Post,
                $"/api/Printers/{foreignPrinterId}/images/1/set-as-default"), ct);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
