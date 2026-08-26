using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Services;
using SkiaSharp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

public class FilamentImageHydrationTests(CustomWebApplicationFactory factory)
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

    private static MemoryStream MakePng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var ms = new MemoryStream(data.ToArray());
        ms.Position = 0;
        return ms;
    }

    private async Task<(Guid filamentId, List<int> imageIds, long userId, string displayName)> SeedFilamentWithImagesAsync(
        int count, CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var service = scope.ServiceProvider.GetRequiredService<IFilamentImageService>();

        // Read the user from THIS factory's database rather than the seeder's static:
        // test classes run in parallel, each with its own factory and database.
        var userId = await context.Users
            .Where(u => u.OAuthUserId == IntegrationTestSeeder.TestUserOAuthId)
            .Select(u => u.Id)
            .FirstAsync(ct);

        var displayName = $"Hydration Spool {Guid.NewGuid()}";
        var filament = new Filament
        {
            Id = Guid.NewGuid(),
            DisplayName = displayName,
            MaterialType = "PLA",
            MaterialCategoryNickname = "filament",
            MaterialDensityGramPerCubicCm = 1.24,
            InitialNominalWeightMg = 1000000,
            Source = Filament.SourceMeasurement.Weight,
            IsActive = true,
            CreatedById = userId,
            UpdatedById = userId
        };
        context.Filaments.Add(filament);
        await context.SaveChangesAsync(ct);

        var imageIds = new List<int>();
        for (var i = 0; i < count; i++)
        {
            using var png = MakePng(100, 100);
            imageIds.Add((await service.AddImageAsync(filament.Id, png, userId, ct)).Id);
        }

        return (filament.Id, imageIds, userId, displayName);
    }

    /// <summary>
    /// A public print whose filament usage points at a filament that HAS images, which is
    /// the exact shape the leak would take.
    /// </summary>
    private async Task<(long printId, string filamentName)> SeedPublicPrintWithFilamentUsageAndImagesAsync(
        CancellationToken ct)
    {
        var (filamentId, _, userId, displayName) = await SeedFilamentWithImagesAsync(1, ct);

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var printerId = await context.Printers
            .Where(p => p.UserId == userId)
            .Select(p => p.Id)
            .FirstAsync(ct);

        var print = new Print
        {
            Title = $"Public Print {Guid.NewGuid()}",
            PrinterId = printerId,
            ViewStatus = Print.PrintViewStatus.Public,
            StartDate = DateTime.UtcNow.AddHours(-2),
            CreatedById = userId,
            UpdatedById = userId
        };
        context.Prints.Add(print);
        await context.SaveChangesAsync(ct);

        context.PrintFilament.Add(new PrintFilament
        {
            PrintId = print.Id,
            FilamentId = filamentId
        });
        await context.SaveChangesAsync(ct);

        return (print.Id, displayName);
    }

    private async Task<FilamentDetailDto> GetFilamentDetailAsync(Guid filamentId, CancellationToken ct)
    {
        var resp = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, $"/api/Filaments/{filamentId}"), ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<FilamentDetailDto>(ct))!;
    }

    [Fact]
    public async Task AnonymousPrintDetail_DoesNotExposeFilamentImageUrls()
    {
        // FilamentSummaryDto nests inside PrintFilamentSummaryDto, which PrintProfile
        // maps into print detail — and GET /Prints/{id} is [AllowAnonymous]. If signing
        // were ever an AutoMapper member mapping, every public print page would leak
        // signed URLs to the owner's private spool photos. This test is the guard.
        var (printId, filamentName) = await SeedPublicPrintWithFilamentUsageAndImagesAsync(
            TestContext.Current.CancellationToken);

        var response = await _client.GetAsync($"/api/Prints/{printId}",
            TestContext.Current.CancellationToken); // no auth header
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Guards against the whole test passing vacuously: the two DoesNotContain
        // assertions below mean nothing unless the filament summary is really in there.
        Assert.Contains(filamentName, json, StringComparison.Ordinal);

        Assert.DoesNotContain("filamentimages", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sig=", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OwnerFilamentList_DoesExposeASignedThumbnailUrl()
    {
        // The negative test above passes trivially if hydration never runs at all.
        // This is its positive counterpart.
        await SeedFilamentWithImagesAsync(1, TestContext.Current.CancellationToken);

        var resp = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, "/api/Filaments?pageSize=100"),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();

        var page = await resp.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>(
            TestContext.Current.CancellationToken);

        Assert.Contains(page!.Items, i => !string.IsNullOrEmpty(i.DefaultImageThumbnailUrl));
    }

    [Fact]
    public async Task FilamentDetail_ExposesSignedUrlsForEveryImage()
    {
        var (filamentId, imageIds, _, _) = await SeedFilamentWithImagesAsync(
            2, TestContext.Current.CancellationToken);

        var detail = await GetFilamentDetailAsync(filamentId, TestContext.Current.CancellationToken);

        Assert.Equal(2, detail.Images.Count);
        Assert.Equal(imageIds.OrderBy(i => i), detail.Images.Select(i => i.Id).OrderBy(i => i));
        Assert.All(detail.Images, i => Assert.False(string.IsNullOrEmpty(i.Url)));
        Assert.All(detail.Images, i => Assert.False(string.IsNullOrEmpty(i.ThumbnailUrl)));
        Assert.Single(detail.Images, i => i.IsDefault);
    }

    [Fact]
    public async Task PutFilament_WithImagesPayload_DoesNotAlterImageRows()
    {
        // PUT /Filaments/{id} takes FilamentDetailDto as its REQUEST body and maps it
        // back over the tracked entity. Without an inbound ignore this is an injection path.
        var (filamentId, imageIds, _, _) = await SeedFilamentWithImagesAsync(
            2, TestContext.Current.CancellationToken);

        var detail = await GetFilamentDetailAsync(filamentId, TestContext.Current.CancellationToken);
        detail.Images = new List<FilamentImageDto>
        {
            new() { Id = 9999, Url = "https://evil.example.com/x.jpg", IsDefault = true }
        };

        var req = AuthenticatedRequest(HttpMethod.Put, $"/api/Filaments/{filamentId}");
        req.Content = JsonContent.Create(detail);
        var resp = await _client.SendAsync(req, TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var stored = await context.FilamentImages
            .Where(fi => fi.FilamentId == filamentId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, stored.Count);
        Assert.Equal(imageIds.OrderBy(i => i), stored.Select(s => s.Id).OrderBy(i => i));
    }

    [Fact]
    public async Task FilamentList_ClampsPageSize()
    {
        // PagedRequest.PageSize is an unconstrained int passed straight to Take().
        var resp = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, "/api/Filaments?pageSize=100000"),
            TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();

        var page = await resp.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>(
            TestContext.Current.CancellationToken);

        // Note: PageSize lives on PagedList.Paging, not on PagedList itself.
        Assert.True(page!.Paging.PageSize <= 100);
    }

    [Fact]
    public async Task SigningFailure_YieldsNullUrlRatherThanFailingTheList()
    {
        // The DTO documents Url as nullable "if signing failed"; hydration must honor
        // that rather than letting one failure kill the whole owner list.
        await SeedFilamentWithImagesAsync(1, TestContext.Current.CancellationToken);

        using var failing = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IBlobStorageService));
                if (descriptor is not null) services.Remove(descriptor);
                services.AddSingleton<IBlobStorageService, ThrowingSasBlobStorageService>();
            }));

        var client = failing.CreateClient();
        var resp = await client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, "/api/Filaments?pageSize=100"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var page = await resp.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>(
            TestContext.Current.CancellationToken);

        Assert.All(page!.Items, i => Assert.Null(i.DefaultImageThumbnailUrl));
    }

    /// <summary>
    /// Signs nothing and throws instead, so the hydration failure path is exercised for real
    /// rather than by an unreachable catch. Composition, not inheritance: hiding a member with
    /// `new` would be bypassed entirely by the interface dispatch the service actually uses.
    /// </summary>
    private sealed class ThrowingSasBlobStorageService : IBlobStorageService
    {
        private readonly InMemoryBlobStorageService _inner = new();

        public Task<BlobUploadResult> UploadAsync(string containerName, string blobName, Stream stream)
            => _inner.UploadAsync(containerName, blobName, stream);

        public Task<Uri> GenerateSasUploadUrlAsync(string containerName, string blobName, TimeSpan expiry)
            => _inner.GenerateSasUploadUrlAsync(containerName, blobName, expiry);

        public Task<Uri> GenerateSasDownloadUrlAsync(
            string containerName, string blobName, string contentType,
            string originalFileName, TimeSpan expiry)
            => _inner.GenerateSasDownloadUrlAsync(containerName, blobName, contentType, originalFileName, expiry);

        public Task<Uri> GenerateSasInlineUrlAsync(
            string containerName, string blobName, string contentType,
            TimeSpan bucketSize, TimeSpan cacheControlMaxAge)
            => throw new InvalidOperationException("signing is unavailable");

        public Task<(Stream stream, string fileName)?> DownloadAsync(string containerName, string blobName)
            => _inner.DownloadAsync(containerName, blobName);

        public Task DeleteBlobAsync(string containerName, string blobName)
            => _inner.DeleteBlobAsync(containerName, blobName);
    }
}
