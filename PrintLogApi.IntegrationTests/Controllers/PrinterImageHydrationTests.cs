using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Printer;
using PrintLogApi.Services;
using SkiaSharp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// The read side: detail hydration, the printer list thumbnail signed after the cache, and
/// the authenticated whole-user thumbnail map.
/// </summary>
public class PrinterImageHydrationTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private const string ForeignUserOAuthId = "auth0|printer-hydration-foreign-user";

    private static HttpRequestMessage Request(HttpMethod method, string url, string oauthId)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, oauthId);
        return request;
    }

    private static HttpRequestMessage AuthenticatedRequest(HttpMethod method, string url)
        => Request(method, url, IntegrationTestSeeder.TestUserOAuthId);

    private static byte[] PngBytes()
    {
        using var bitmap = new SKBitmap(200, 100);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static MultipartFormDataContent ValidMultipart()
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(PngBytes());
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(content, "file", "printer.png");
        return form;
    }

    private InMemoryBlobStorageService Blobs()
        => (InMemoryBlobStorageService)_factory.Services.GetRequiredService<IBlobStorageService>();

    private ControllableImageProcessingService ImageProcessing()
        => (ControllableImageProcessingService)_factory.Services.GetRequiredService<IImageProcessingService>();

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
            Name = $"Hydration Printer {Guid.NewGuid()}",
            Make = "Prusa",
            Model = "MK4",
            UserId = userId.Value,
            IsActive = true
        };
        context.Printers.Add(printer);
        await context.SaveChangesAsync(ct);
        return printer.Id;
    }

    private async Task<PrinterImageDto> UploadAsync(long printerId, CancellationToken ct, string? oauthId = null)
    {
        var req = Request(HttpMethod.Post, $"/api/Printers/{printerId}/images",
            oauthId ?? IntegrationTestSeeder.TestUserOAuthId);
        req.Content = ValidMultipart();

        var resp = await _client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<PrinterImageDto>(ct))!;
    }

    // -- Detail hydration ---------------------------------------------------------------

    [Fact]
    public async Task HydrateDetail_NoDefaultFlagged_TreatsLowestDisplayOrderAsDefault()
    {
        // The filtered unique index enforces AT MOST one default. A delete interleaved with
        // an add can leave a non-empty set with none, which the index cannot prevent.
        var ct = TestContext.Current.CancellationToken;
        var printerId = await CreatePrinterAsync(ct);
        var first = await UploadAsync(printerId, ct);
        var second = await UploadAsync(printerId, ct);

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var printerService = scope.ServiceProvider.GetRequiredService<IPrinterService>();

        var tracked = await context.PrinterImages.SingleAsync(pi => pi.Id == first.Id, ct);
        tracked.IsDefault = false;
        await context.SaveChangesAsync(ct);

        var dto = new PrinterDetailDto { Id = printerId };
        await printerService.HydrateDetailImageUrlsAsync(dto, ct);

        Assert.True(dto.Images!.Single(i => i.Id == first.Id).IsDefault);
        Assert.False(dto.Images!.Single(i => i.Id == second.Id).IsDefault);
    }

    [Fact]
    public async Task HydrateDetail_NullId_DoesNothingRatherThanQuerying()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var printerService = scope.ServiceProvider.GetRequiredService<IPrinterService>();

        var dto = new PrinterDetailDto { Id = null };
        await printerService.HydrateDetailImageUrlsAsync(dto, ct);

        Assert.Null(dto.Images);
    }

    [Fact]
    public async Task HydrateDetail_NoThumbnail_SignsOriginalAsItsOwnContentType()
    {
        // Signing a JPEG original as "image/webp" would set a response content type the
        // bytes contradict. The URL does not carry the content type, so only the recording
        // double can catch this.
        var ct = TestContext.Current.CancellationToken;
        var printerId = await CreatePrinterAsync(ct);

        ImageProcessing().NextThumbnailIsNull = true;
        await UploadAsync(printerId, ct);

        using var scope = _factory.Services.CreateScope();
        var printerService = scope.ServiceProvider.GetRequiredService<IPrinterService>();

        var dto = new PrinterDetailDto { Id = printerId };
        await printerService.HydrateDetailImageUrlsAsync(dto, ct);

        Assert.Equal("image/jpeg", Blobs().LastSignedContentType);

        // The thumbnail URL falls back to the original rather than being null.
        Assert.Equal(dto.Images!.Single().Url, dto.Images!.Single().ThumbnailUrl);
    }

    // -- Printer list -------------------------------------------------------------------

    [Fact]
    public async Task GetSummary_WithDefaultImage_ReturnsSignedThumbnailUrl()
    {
        var ct = TestContext.Current.CancellationToken;
        var printerId = await CreatePrinterAsync(ct);
        await UploadAsync(printerId, ct);

        var resp = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, "/api/Printers/summary?pageSize=100"), ct);
        resp.EnsureSuccessStatusCode();

        var page = await resp.Content.ReadFromJsonAsync<PagedList<PrinterSummarySimpleDto>>(ct);
        var row = page!.Items.Single(p => p.Id == printerId);

        Assert.Contains("sig=", row.DefaultImageThumbnailUrl);
    }

    [Fact]
    public async Task GetSummary_SignsAfterEveryCacheHit_AndLeavesTheCachedInstanceUnsigned()
    {
        // A URL-equality assertion cannot detect in-place mutation of the cached DTO,
        // because bucketed signing deliberately returns byte-identical URLs within a
        // window. Vary the signature instead: an implementation that signed once and
        // mutated the cached instance would still return the FIRST signature on the
        // second request.
        var ct = TestContext.Current.CancellationToken;
        var printerId = await CreatePrinterAsync(ct);
        await UploadAsync(printerId, ct);

        var blobs = Blobs();
        var url = $"/api/Printers/summary?pageSize=100";

        blobs.NextSignature = "first-signature";
        var warm = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, url), ct);
        warm.EnsureSuccessStatusCode();

        blobs.NextSignature = "second-signature";
        var resp = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, url), ct);
        resp.EnsureSuccessStatusCode();

        var page = await resp.Content.ReadFromJsonAsync<PagedList<PrinterSummarySimpleDto>>(ct);
        var row = page!.Items.Single(p => p.Id == printerId);

        Assert.Contains("second-signature", row.DefaultImageThumbnailUrl);
    }

    [Fact]
    public async Task GetSummary_DoesNotLeakTheBlobPath()
    {
        var ct = TestContext.Current.CancellationToken;
        var printerId = await CreatePrinterAsync(ct);
        await UploadAsync(printerId, ct);

        var resp = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, "/api/Printers/summary?pageSize=100"), ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        Assert.DoesNotContain("defaultImageBlobPath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("defaultImageContentType", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSummary_WithoutImages_ReturnsNullThumbnailUrl()
    {
        var ct = TestContext.Current.CancellationToken;
        var printerId = await CreatePrinterAsync(ct);

        var resp = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, "/api/Printers/summary?pageSize=100"), ct);
        resp.EnsureSuccessStatusCode();

        var page = await resp.Content.ReadFromJsonAsync<PagedList<PrinterSummarySimpleDto>>(ct);

        Assert.Null(page!.Items.Single(p => p.Id == printerId).DefaultImageThumbnailUrl);
    }

    // -- Thumbnail map ------------------------------------------------------------------

    [Fact]
    public async Task GetThumbnails_Anonymous_IsRejected()
    {
        // The privacy rule is structural: the endpoint has no anonymous variant, so an
        // anonymous visitor gets an empty map and therefore no avatars anywhere.
        var ct = TestContext.Current.CancellationToken;
        var anonymous = _factory.CreateClient();

        var resp = await anonymous.GetAsync("/api/Printers/thumbnails", ct);

        Assert.True(resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Expected 401/403 for an anonymous caller, got {(int)resp.StatusCode}.");
    }

    [Fact]
    public async Task GetThumbnails_ReturnsSignedUrlForOwnPrinter()
    {
        var ct = TestContext.Current.CancellationToken;
        var printerId = await CreatePrinterAsync(ct);
        await UploadAsync(printerId, ct);

        var resp = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, "/api/Printers/thumbnails"), ct);
        resp.EnsureSuccessStatusCode();

        var map = await resp.Content.ReadFromJsonAsync<List<PrinterThumbnailDto>>(ct);

        Assert.Contains("sig=", map!.Single(t => t.PrinterId == printerId).ThumbnailUrl);
    }

    [Fact]
    public async Task GetThumbnails_ExcludesForeignPrinters_EvenWhenTheCallerUploadedTheImage()
    {
        // The foreign image is deliberately stamped with the caller as CreatedById. A
        // predicate on CreatedById would wrongly include it, and because this endpoint
        // returns a whole-user map, that mistake leaks every default printer image in the
        // database while still passing every happy-path test.
        var ct = TestContext.Current.CancellationToken;
        var foreignPrinterId = await CreatePrinterAsync(ct, ForeignUserOAuthId);
        var image = await UploadAsync(foreignPrinterId, ct, ForeignUserOAuthId);

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var callerId = await context.Users
            .Where(u => u.OAuthUserId == IntegrationTestSeeder.TestUserOAuthId)
            .Select(u => u.Id).FirstAsync(ct);

        var tracked = await context.PrinterImages.SingleAsync(pi => pi.Id == image.Id, ct);
        tracked.CreatedById = callerId;
        await context.SaveChangesAsync(ct);

        var resp = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, "/api/Printers/thumbnails"), ct);
        resp.EnsureSuccessStatusCode();

        var map = await resp.Content.ReadFromJsonAsync<List<PrinterThumbnailDto>>(ct);

        Assert.DoesNotContain(map!, t => t.PrinterId == foreignPrinterId);
    }

    [Fact]
    public async Task GetThumbnails_ReturnsOneEntryPerPrinter()
    {
        var ct = TestContext.Current.CancellationToken;
        var printerId = await CreatePrinterAsync(ct);
        await UploadAsync(printerId, ct);
        await UploadAsync(printerId, ct);

        var resp = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, "/api/Printers/thumbnails"), ct);
        resp.EnsureSuccessStatusCode();

        var map = await resp.Content.ReadFromJsonAsync<List<PrinterThumbnailDto>>(ct);

        Assert.Single(map!, t => t.PrinterId == printerId);
    }

    [Fact]
    public async Task GetThumbnails_NoThumbnail_SignsOriginalAsItsOwnContentType()
    {
        var ct = TestContext.Current.CancellationToken;
        var printerId = await CreatePrinterAsync(ct);

        ImageProcessing().NextThumbnailIsNull = true;
        await UploadAsync(printerId, ct);

        var blobs = Blobs();
        blobs.NextSignature = "content-type-probe";

        var resp = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, "/api/Printers/thumbnails"), ct);
        resp.EnsureSuccessStatusCode();

        Assert.Equal("image/jpeg", blobs.LastSignedContentType);
    }
}
