using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Services;
using SkiaSharp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

public class FilamentImageLifecycleTests(CustomWebApplicationFactory factory)
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

    private static async Task<Guid> SeedFilamentWithImagesAsync(
        IServiceScope scope, int count, long userId, CancellationToken ct)
    {
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var service = scope.ServiceProvider.GetRequiredService<IFilamentImageService>();

        var filament = new Filament
        {
            Id = Guid.NewGuid(),
            DisplayName = $"Lifecycle Spool {Guid.NewGuid()}",
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

        for (var i = 0; i < count; i++)
        {
            using var png = MakePng(100, 100);
            await service.AddImageAsync(filament.Id, png, userId, ct);
        }

        return filament.Id;
    }

    /// <summary>
    /// A user of this test's own, so deleting them cannot disturb the shared seeded user
    /// that every other test in this assembly depends on.
    /// </summary>
    private static async Task<User> SeedUserWithFilamentImagesAsync(
        IServiceScope scope, CancellationToken ct)
    {
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var user = new User
        {
            OAuthUserId = $"auth0|lifecycle-{Guid.NewGuid()}",
            DisplayName = "Lifecycle User"
        };
        context.Users.Add(user);
        await context.SaveChangesAsync(ct);

        await SeedFilamentWithImagesAsync(scope, 2, user.Id, ct);

        return user;
    }

    [Fact]
    public async Task DeletingAFilament_RemovesItsImagesFilesAndBlobs()
    {
        // Without this, the new FK either blocks the delete outright or orphans every
        // blob and File row.
        using var scope = _factory.Services.CreateScope();
        var filamentService = scope.ServiceProvider.GetRequiredService<IFilamentService>();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var blobs = (InMemoryBlobStorageService)scope.ServiceProvider
            .GetRequiredService<IBlobStorageService>();

        var userId = await context.Users
            .Where(u => u.OAuthUserId == IntegrationTestSeeder.TestUserOAuthId)
            .Select(u => u.Id)
            .FirstAsync(TestContext.Current.CancellationToken);

        var filamentId = await SeedFilamentWithImagesAsync(
            scope, 2, userId, TestContext.Current.CancellationToken);

        var paths = await context.FilamentImages
            .AsNoTracking()
            .Where(fi => fi.FilamentId == filamentId)
            .Select(fi => new { fi.File.Path, ThumbPath = fi.ThumbnailFile!.Path })
            .ToListAsync(TestContext.Current.CancellationToken);

        // Projected as a pair and flattened in memory: EF cannot translate a SelectMany
        // over an inline array of a Guid and a Guid?.
        var fileIdPairs = await context.FilamentImages
            .AsNoTracking()
            .Where(fi => fi.FilamentId == filamentId)
            .Select(fi => new { fi.FileId, fi.ThumbnailFileId })
            .ToListAsync(TestContext.Current.CancellationToken);

        var fileIds = fileIdPairs
            .SelectMany(pair => new[] { pair.FileId, pair.ThumbnailFileId })
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();

        Assert.NotEmpty(paths);
        Assert.All(paths, p =>
            Assert.Contains($"{BlobContainers.FilamentImages}/{p.Path}", blobs.Blobs.Keys));

        await filamentService.DeleteFilament(filamentId);

        Assert.Empty(await context.FilamentImages
            .Where(fi => fi.FilamentId == filamentId)
            .ToListAsync(TestContext.Current.CancellationToken));

        Assert.Empty(await context.Files
            .Where(f => fileIds.Contains(f.Id))
            .ToListAsync(TestContext.Current.CancellationToken));

        foreach (var p in paths)
        {
            Assert.DoesNotContain($"{BlobContainers.FilamentImages}/{p.Path}", blobs.Blobs.Keys);
            Assert.DoesNotContain($"{BlobContainers.FilamentImages}/{p.ThumbPath}", blobs.Blobs.Keys);
        }
    }

    [Fact]
    public async Task DeletingAUser_RemovesFilamentImagesFilesAndBlobs()
    {
        // UserDeletionService deletes filaments via ExecuteDeleteAsync, which bypasses
        // the change tracker and hits the FK directly. Cleanup must run first.
        using var scope = _factory.Services.CreateScope();
        var deletion = scope.ServiceProvider.GetRequiredService<IUserDeletionService>();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var blobs = (InMemoryBlobStorageService)scope.ServiceProvider
            .GetRequiredService<IBlobStorageService>();

        User user;
        using (var seedScope = _factory.Services.CreateScope())
        {
            // Seeding gets its own scope on purpose. DeleteAllDataForUser issues
            // ExecuteDeleteAsync, which bypasses the change tracker; rows deleted out from
            // under a context that still tracks them surface as a concurrency exception.
            // Production never hits this - deletion runs in a scope of its own.
            user = await SeedUserWithFilamentImagesAsync(seedScope, TestContext.Current.CancellationToken);
        }

        var paths = await context.FilamentImages
            .AsNoTracking()
            .Where(fi => fi.Filament.CreatedById == user.Id)
            .Select(fi => new { fi.File.Path, ThumbPath = fi.ThumbnailFile!.Path })
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(paths);

        await deletion.DeleteAllDataForUser(user);

        Assert.Empty(await context.FilamentImages
            .Where(fi => fi.Filament.CreatedById == user.Id)
            .ToListAsync(TestContext.Current.CancellationToken));

        foreach (var p in paths)
        {
            Assert.DoesNotContain($"{BlobContainers.FilamentImages}/{p.Path}", blobs.Blobs.Keys);
            Assert.DoesNotContain($"{BlobContainers.FilamentImages}/{p.ThumbPath}", blobs.Blobs.Keys);
        }
    }
}
