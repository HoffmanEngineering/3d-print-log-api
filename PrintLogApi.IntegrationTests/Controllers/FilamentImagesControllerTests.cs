using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Services;
using SkiaSharp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

public class FilamentImagesControllerTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private const string ForeignUserOAuthId = "auth0|filament-image-foreign-user";

    private static HttpRequestMessage AuthenticatedRequest(HttpMethod method, string url)
        => Request(method, url, IntegrationTestSeeder.TestUserOAuthId);

    private static HttpRequestMessage Request(HttpMethod method, string url, string oauthId)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, oauthId);
        return request;
    }

    private static byte[] PngBytes(int width, int height)
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

    /// <summary>
    /// Creates a filament owned by <paramref name="oauthId"/>, adding that user first if this
    /// database does not have them yet.
    /// </summary>
    private async Task<Guid> CreateFilamentAsync(CancellationToken ct, string? oauthId = null)
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

        var filament = new Filament
        {
            Id = Guid.NewGuid(),
            DisplayName = $"Endpoint Spool {Guid.NewGuid()}",
            MaterialType = "PLA",
            MaterialCategoryNickname = "filament",
            MaterialDensityGramPerCubicCm = 1.24,
            InitialNominalWeightMg = 1000000,
            Source = Filament.SourceMeasurement.Weight,
            IsActive = true,
            CreatedById = userId.Value,
            UpdatedById = userId.Value
        };
        context.Filaments.Add(filament);
        await context.SaveChangesAsync(ct);
        return filament.Id;
    }

    private async Task<FilamentImageDto> UploadAsync(Guid filamentId, CancellationToken ct)
    {
        var req = AuthenticatedRequest(HttpMethod.Post, $"/api/Filaments/{filamentId}/images");
        req.Content = Form(PngBytes(200, 100), "image/png", "spool.png");

        var resp = await _client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<FilamentImageDto>(ct))!;
    }

    [Fact]
    public async Task Upload_ValidPng_Returns201WithSignedUrls()
    {
        var filamentId = await CreateFilamentAsync(TestContext.Current.CancellationToken);

        var req = AuthenticatedRequest(HttpMethod.Post, $"/api/Filaments/{filamentId}/images");
        req.Content = Form(PngBytes(200, 100), "image/png", "spool.png");

        var resp = await _client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var dto = await resp.Content.ReadFromJsonAsync<FilamentImageDto>(
            TestContext.Current.CancellationToken);

        Assert.NotNull(dto);
        Assert.True(dto!.Id > 0);
        Assert.False(string.IsNullOrEmpty(dto.Url));
        Assert.False(string.IsNullOrEmpty(dto.ThumbnailUrl));
        Assert.True(dto.IsDefault); // first image
    }

    [Fact]
    public async Task Upload_TextFileLabeledPng_Returns400()
    {
        // The client's declared content type is not trusted; decoding is the validation.
        var filamentId = await CreateFilamentAsync(TestContext.Current.CancellationToken);

        var req = AuthenticatedRequest(HttpMethod.Post, $"/api/Filaments/{filamentId}/images");
        req.Content = Form("not an image at all"u8.ToArray(), "image/png", "spool.png");

        var resp = await _client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Upload_Gif_Returns400()
    {
        var filamentId = await CreateFilamentAsync(TestContext.Current.CancellationToken);

        // A minimal but genuinely valid 1x1 GIF87a, so the rejection comes from the format
        // allowlist rather than from a failed decode.
        var gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

        var req = AuthenticatedRequest(HttpMethod.Post, $"/api/Filaments/{filamentId}/images");
        req.Content = Form(gif, "image/gif", "spool.gif");

        var resp = await _client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Upload_Over10Mb_Returns400()
    {
        var filamentId = await CreateFilamentAsync(TestContext.Current.CancellationToken);

        // Rejected on Length alone, before any decode, so the bytes need not be an image.
        var oversized = new byte[(10 * 1024 * 1024) + 1];

        var req = AuthenticatedRequest(HttpMethod.Post, $"/api/Filaments/{filamentId}/images");
        req.Content = Form(oversized, "image/png", "huge.png");

        var resp = await _client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Upload_PastPerFilamentQuota_Returns400NotAServerError()
    {
        // The service throws for quota; the action has to translate that. Both quota
        // exceptions matter here - ArgumentException for the per-filament cap and
        // BadRequestException for the account byte cap - and this app has no global
        // exception-to-status mapping, so an uncaught one is a 500, not a 400.
        var filamentId = await CreateFilamentAsync(TestContext.Current.CancellationToken);

        for (var i = 0; i < SubscriptionLimits.FreeMaxImagesPerFilament; i++)
            await UploadAsync(filamentId, TestContext.Current.CancellationToken);

        var req = AuthenticatedRequest(HttpMethod.Post, $"/api/Filaments/{filamentId}/images");
        req.Content = Form(PngBytes(50, 50), "image/png", "spool.png");

        var resp = await _client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Upload_ByNonOwner_Returns404()
    {
        // 404 rather than 403: the endpoint must not confirm that the filament exists.
        var foreignFilamentId = await CreateFilamentAsync(
            TestContext.Current.CancellationToken, ForeignUserOAuthId);

        var req = AuthenticatedRequest(HttpMethod.Post, $"/api/Filaments/{foreignFilamentId}/images");
        req.Content = Form(PngBytes(50, 50), "image/png", "spool.png");

        var resp = await _client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetImage_Returns302WithSignedLocation()
    {
        var filamentId = await CreateFilamentAsync(TestContext.Current.CancellationToken);
        var image = await UploadAsync(filamentId, TestContext.Current.CancellationToken);

        // The default HttpClient follows redirects, which would turn this into a request
        // against a fake blob host. Assert on the 302 itself.
        using var noRedirect = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });

        var resp = await noRedirect.SendAsync(
            Request(HttpMethod.Get, $"/api/Filaments/{filamentId}/images/{image.Id}",
                IntegrationTestSeeder.TestUserOAuthId),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        Assert.NotNull(resp.Headers.Location);
        Assert.Contains(BlobContainers.FilamentImages, resp.Headers.Location!.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetImage_ForForeignFilament_Returns404()
    {
        var filamentId = await CreateFilamentAsync(TestContext.Current.CancellationToken);
        var image = await UploadAsync(filamentId, TestContext.Current.CancellationToken);

        // Same image id, different caller. Ownership is part of the lookup predicate.
        await CreateFilamentAsync(TestContext.Current.CancellationToken, ForeignUserOAuthId);

        var resp = await _client.SendAsync(
            Request(HttpMethod.Get, $"/api/Filaments/{filamentId}/images/{image.Id}",
                ForeignUserOAuthId),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Reorder_WithForeignIds_Returns400()
    {
        var filamentId = await CreateFilamentAsync(TestContext.Current.CancellationToken);
        await UploadAsync(filamentId, TestContext.Current.CancellationToken);
        await UploadAsync(filamentId, TestContext.Current.CancellationToken);

        var req = AuthenticatedRequest(HttpMethod.Put, $"/api/Filaments/{filamentId}/images/reorder");
        req.Content = JsonContent.Create(new[] { 987654, 987655 });

        var resp = await _client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task SetAsDefault_MovesTheDefault()
    {
        var filamentId = await CreateFilamentAsync(TestContext.Current.CancellationToken);
        var first = await UploadAsync(filamentId, TestContext.Current.CancellationToken);
        var second = await UploadAsync(filamentId, TestContext.Current.CancellationToken);

        Assert.True(first.IsDefault);
        Assert.False(second.IsDefault);

        var resp = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Post,
                $"/api/Filaments/{filamentId}/images/{second.Id}/set-as-default"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var images = await context.FilamentImages
            .AsNoTracking()
            .Where(fi => fi.FilamentId == filamentId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(images, i => i.IsDefault);
        Assert.True(images.First(i => i.Id == second.Id).IsDefault);
    }
}
