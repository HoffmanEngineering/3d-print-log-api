using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Services;
using SkiaSharp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

public class FilamentImageServiceTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory = factory;

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

    /// <summary>
    /// Creates a filament of its own rather than reusing a seeded one, so these tests do not
    /// depend on running before or after their siblings.
    /// </summary>
    private async Task<(Guid filamentId, List<int> imageIds, long userId)> SeedFilamentWithImagesAsync(
        IServiceScope scope, int count, CancellationToken ct)
    {
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var service = scope.ServiceProvider.GetRequiredService<IFilamentImageService>();
        // Read the user from THIS factory's database rather than the seeder's static.
        // xUnit runs test classes in parallel, each with its own factory and database,
        // and the static holds whichever one seeded last.
        var userId = await context.Users
            .Select(u => u.Id)
            .FirstAsync(ct);

        var filament = new Filament
        {
            Id = Guid.NewGuid(),
            DisplayName = $"Image Test Spool {Guid.NewGuid()}",
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
            var image = await service.AddImageAsync(filament.Id, png, userId, ct);
            imageIds.Add(image.Id);
        }

        return (filament.Id, imageIds, userId);
    }

    [Fact]
    public async Task Reorder_WithIncompleteIdSet_Throws()
    {
        // ProjectService silently ignores unknown IDs, turning a client bug into silent
        // data drift. Filament follows the print contract instead.
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFilamentImageService>();
        var (filamentId, imageIds, userId) = await SeedFilamentWithImagesAsync(
            scope, 3, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ReorderImagesAsync(filamentId, new[] { imageIds[0] }, userId,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reorder_WithDuplicateIds_Throws()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFilamentImageService>();
        var (filamentId, imageIds, userId) = await SeedFilamentWithImagesAsync(
            scope, 2, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ReorderImagesAsync(filamentId, new[] { imageIds[0], imageIds[0] }, userId,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FirstImage_BecomesTheOnlyDefault()
    {
        using var scope = _factory.Services.CreateScope();
        var (filamentId, imageIds, _) = await SeedFilamentWithImagesAsync(
            scope, 2, TestContext.Current.CancellationToken);
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var images = await context.FilamentImages
            .Where(fi => fi.FilamentId == filamentId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(images, i => i.IsDefault);
        Assert.True(images.First(i => i.Id == imageIds[0]).IsDefault);
    }

    [Fact]
    public async Task Delete_RemovesImageRowFileRowsAndBlobs()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFilamentImageService>();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var blobs = (InMemoryBlobStorageService)scope.ServiceProvider
            .GetRequiredService<IBlobStorageService>();
        var (filamentId, imageIds, userId) = await SeedFilamentWithImagesAsync(
            scope, 2, TestContext.Current.CancellationToken);

        var image = await context.FilamentImages
            .AsNoTracking()
            .Include(fi => fi.File)
            .Include(fi => fi.ThumbnailFile)
            .FirstAsync(fi => fi.Id == imageIds[1], TestContext.Current.CancellationToken);

        // Capture the ACTUAL blob names. Blob names are random GUIDs, so asserting that
        // no key contains the numeric image ID would pass even without any deletion.
        var expectedGone = new[] { image.File.Path, image.ThumbnailFile?.Path }
            .Where(p => p is not null)
            .Select(p => $"{BlobContainers.FilamentImages}/{p}")
            .ToList();
        Assert.NotEmpty(expectedGone);
        Assert.All(expectedGone, key => Assert.Contains(key, blobs.Blobs.Keys));

        var fileIds = new[] { image.FileId, image.ThumbnailFileId }
            .Where(g => g.HasValue).Select(g => g!.Value).ToList();

        await service.DeleteImageAsync(filamentId, imageIds[1], userId,
            TestContext.Current.CancellationToken);

        Assert.Empty(await context.FilamentImages
            .Where(fi => fi.Id == imageIds[1]).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await context.Files
            .Where(f => fileIds.Contains(f.Id)).ToListAsync(TestContext.Current.CancellationToken));
        Assert.All(expectedGone, key => Assert.DoesNotContain(key, blobs.Blobs.Keys));
    }

    [Fact]
    public async Task DeletingTheDefault_PromotesAnother()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFilamentImageService>();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var (filamentId, imageIds, userId) = await SeedFilamentWithImagesAsync(
            scope, 2, TestContext.Current.CancellationToken);

        await service.DeleteImageAsync(filamentId, imageIds[0], userId,
            TestContext.Current.CancellationToken);

        var remaining = await context.FilamentImages
            .Where(fi => fi.FilamentId == filamentId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(remaining);
        Assert.True(remaining[0].IsDefault);
    }

    [Fact]
    public async Task SetDefault_LeavesExactlyOneDefault()
    {
        // Must not violate the filtered unique index mid-operation: EF emits separate
        // UPDATEs and does not guarantee clear-before-set within one SaveChangesAsync.
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFilamentImageService>();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var (filamentId, imageIds, userId) = await SeedFilamentWithImagesAsync(
            scope, 3, TestContext.Current.CancellationToken);

        await service.SetDefaultImageAsync(filamentId, imageIds[2], userId,
            TestContext.Current.CancellationToken);

        var images = await context.FilamentImages
            .Where(fi => fi.FilamentId == filamentId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(images, i => i.IsDefault);
        Assert.True(images.First(i => i.Id == imageIds[2]).IsDefault);
    }

    [Fact]
    public async Task Upload_PastPerFilamentQuota_Throws()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFilamentImageService>();
        var (filamentId, _, userId) = await SeedFilamentWithImagesAsync(
            scope, SubscriptionLimits.FreeMaxImagesPerFilament, TestContext.Current.CancellationToken);

        using var png = MakePng(50, 50);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AddImageAsync(filamentId, png, userId,
                TestContext.Current.CancellationToken));
    }
}
