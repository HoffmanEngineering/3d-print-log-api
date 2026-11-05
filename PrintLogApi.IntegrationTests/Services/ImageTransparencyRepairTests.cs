using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Project;
using PrintLogApi.Services;
using PrintLogApi.Services.ImageBackfill;
using SkiaSharp;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// The transparency repair: rows the image backfill converted before it kept transparency have
/// a JPEG original with a black background. The repair finds each one's untouched original in
/// the archive container, and when it is transparent, rewrites the row as a PNG original with a
/// social image on white. Each test gets its own blob store, so it lists only its own archive.
/// </summary>
public class ImageTransparencyRepairTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();
    private readonly InMemoryBlobStorageService _blobs = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Arrange helpers ----

    private static byte[] OpaquePngBytes(int width = 200, int height = 100)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    /// <summary>What the old backfill stored: the transparent pixels flattened to black, as a JPEG.</summary>
    private static byte[] BlackBackedJpeg(byte[] source)
    {
        using var decoded = SKBitmap.Decode(source);
        using var image = SKImage.FromBitmap(decoded);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static HttpRequestMessage AuthenticatedRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        return request;
    }

    private async Task<long> CreatePrintAsync()
    {
        var request = AuthenticatedRequest(HttpMethod.Post, "/api/Prints");
        request.Content = JsonContent.Create(new AddPrintDTO
        {
            Title = $"Repair Print {Guid.NewGuid()}",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Pending,
            ViewStatus = PrintViewStatus.Public,
            AllowComments = true
        });
        var response = await _client.SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PrintDetailDTO>(Ct))!.Id;
    }

    private async Task<Guid> CreateProjectAsync()
    {
        var request = AuthenticatedRequest(HttpMethod.Post, "/api/Projects");
        request.Content = JsonContent.Create(new AddProjectDto
        {
            Name = $"Repair Project {Guid.NewGuid()}",
            Status = Project.ProjectStatus.InProgress,
            ViewStatus = Project.ProjectViewStatus.Public
        });
        var response = await _client.SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectDetailDto>(Ct))!.Id;
    }

    private sealed record Converted(int ImageId, Guid FileId, string ArchiveName, string OriginalPath, string SocialPath, Guid SocialFileId, Guid ThumbnailFileId);

    private async Task<Models.File> AddFileAsync(PrintLogContext context, long userId, string container, string extension, byte[] bytes, Guid? id = null)
    {
        var fileId = id ?? Guid.NewGuid();
        var name = $"{Guid.NewGuid()}{extension}";
        using (var stream = new MemoryStream(bytes))
            await _blobs.UploadAsync(container, name, stream);
        var file = new Models.File
        {
            Id = fileId,
            Path = $"{container}/{name}",
            Size = bytes.Length,
            CreatedById = userId,
            UpdatedById = userId,
        };
        context.Files.Add(file);
        return file;
    }

    /// <summary>
    /// A print image as the old backfill left it: the untouched upload in the archive under its
    /// legacy name (<c>{fileId}{client extension}</c>), the File row (same id) pointed at a
    /// black-backed JPEG, a thumbnail and a black-backed social JPEG.
    /// </summary>
    private async Task<Converted> SeedConvertedPrintImageAsync(
        long printId, byte[] uploaded, string extension = ".png", bool archive = true, int? width = null)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var userId = await context.Users
            .Where(u => u.OAuthUserId == IntegrationTestSeeder.TestUserOAuthId).Select(u => u.Id).FirstAsync(Ct);

        var fileId = Guid.NewGuid();
        var archiveName = $"{fileId}{extension}";
        if (archive)
        {
            using var stream = new MemoryStream(uploaded);
            await _blobs.UploadAsync(BlobContainers.PrintImagesArchive, archiveName, stream);
        }

        var jpeg = BlackBackedJpeg(uploaded);
        var original = await AddFileAsync(context, userId, BlobContainers.PrintImages, ".jpg", jpeg, fileId);
        var thumbnail = await AddFileAsync(context, userId, BlobContainers.PrintImages, ".webp", [1, 2, 3]);
        var social = await AddFileAsync(context, userId, BlobContainers.PrintImages, ".jpg", jpeg);
        using var decoded = SKBitmap.Decode(uploaded);
        var image = new PrintImage
        {
            PrintId = printId,
            File = original,
            ThumbnailFile = thumbnail,
            SocialFile = social,
            SocialWidth = decoded.Width,
            SocialHeight = decoded.Height,
            ContentType = "image/jpeg",
            Width = width ?? decoded.Width,
            Height = decoded.Height,
            CreatedById = userId,
            UpdatedById = userId,
        };
        context.PrintImages.Add(image);
        await context.SaveChangesAsync(Ct);
        return new Converted(image.Id, fileId, archiveName, original.Path!, social.Path!, social.Id, thumbnail.Id);
    }

    /// <summary>A converted project image: its archive name is random, unrelated to its File id.</summary>
    private async Task<Converted> SeedConvertedProjectImageAsync(Guid projectId, byte[] uploaded)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var userId = await context.Users
            .Where(u => u.OAuthUserId == IntegrationTestSeeder.TestUserOAuthId).Select(u => u.Id).FirstAsync(Ct);

        var archiveName = $"{Guid.NewGuid()}.png";
        using (var stream = new MemoryStream(uploaded))
            await _blobs.UploadAsync(BlobContainers.ProjectImagesArchive, archiveName, stream);

        var jpeg = BlackBackedJpeg(uploaded);
        var original = await AddFileAsync(context, userId, BlobContainers.ProjectImages, ".jpg", jpeg);
        var thumbnail = await AddFileAsync(context, userId, BlobContainers.ProjectImages, ".webp", [1, 2, 3]);
        var social = await AddFileAsync(context, userId, BlobContainers.ProjectImages, ".jpg", jpeg);
        using var decoded = SKBitmap.Decode(uploaded);
        var image = new ProjectImage
        {
            ProjectId = projectId,
            File = original,
            ThumbnailFile = thumbnail,
            SocialFile = social,
            SocialWidth = decoded.Width,
            SocialHeight = decoded.Height,
            ContentType = "image/jpeg",
            Width = decoded.Width,
            Height = decoded.Height,
            CreatedById = userId,
            UpdatedById = userId,
        };
        context.ProjectImages.Add(image);
        await context.SaveChangesAsync(Ct);
        return new Converted(image.Id, original.Id, archiveName, original.Path!, social.Path!, social.Id, thumbnail.Id);
    }

    private async Task<PrintImage> LoadPrintImageAsync(int imageId)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await context.PrintImages
            .Include(pi => pi.File).Include(pi => pi.ThumbnailFile).Include(pi => pi.SocialFile)
            .AsNoTracking()
            .SingleAsync(pi => pi.Id == imageId, Ct);
    }

    private async Task<ProjectImage> LoadProjectImageAsync(int imageId)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await context.ProjectImages
            .Include(pi => pi.File).Include(pi => pi.SocialFile)
            .AsNoTracking()
            .SingleAsync(pi => pi.Id == imageId, Ct);
    }

    private async Task<bool> FileRowExistsAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await context.Files.AnyAsync(f => f.Id == id, Ct);
    }

    private async Task<(TransparencyRepairReport Report, string Log)> RepairAsync(
        TransparencyRepairOptions options, IBlobStorageService? blobs = null, params IInterceptor[] interceptors)
    {
        using var scope = _factory.Services.CreateScope();
        var dbOptions = new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlite(scope.ServiceProvider.GetRequiredService<PrintLogContext>().Database.GetDbConnection())
            .AddInterceptors(interceptors)
            .Options;
        await using var context = new PrintLogContext(dbOptions);
        blobs ??= _blobs;
        var log = new StringWriter();
        var runner = new ImageTransparencyRepairRunner(
            context, blobs, new ImageProcessingService(), new ProcessedImageWriter(context, blobs), log);
        var report = await runner.RunAsync(options, Ct);
        return (report, log.ToString());
    }

    private static readonly TransparencyRepairOptions Apply = new() { Apply = true };

    private static void AssertWhite(SKColor color)
        => Assert.True(color.Red > 245 && color.Green > 245 && color.Blue > 245, $"expected white, got {color}");

    // ---- Tests ----

    [Fact]
    public async Task DryRun_IsTheDefault_AndChangesNothing()
    {
        var printId = await CreatePrintAsync();
        var row = await SeedConvertedPrintImageAsync(printId, ImageBackfillTests.TransparentPngBytes());
        var uploads = _blobs.UploadedBlobNames.Count;

        var (report, log) = await RepairAsync(new TransparencyRepairOptions());

        Assert.False(report.Applied);
        Assert.Equal(1, report.Repaired);
        Assert.Contains($"print-image {row.ImageId}: would be repaired", log, StringComparison.Ordinal);
        var image = await LoadPrintImageAsync(row.ImageId);
        Assert.Equal("image/jpeg", image.ContentType);
        Assert.Equal(row.OriginalPath, image.File.Path);
        Assert.Equal(uploads, _blobs.UploadedBlobNames.Count);
        Assert.Empty(_blobs.DeletedBlobNames);
    }

    [Fact]
    public async Task Apply_FindsAPrintImageByItsFileId_AndRestoresTheTransparency()
    {
        // No log at all: a legacy print blob is named after its File row's id, which the
        // backfill kept, so the archive name leads back to the row.
        var printId = await CreatePrintAsync();
        var uploaded = ImageBackfillTests.TransparentPngBytes();
        var row = await SeedConvertedPrintImageAsync(printId, uploaded);

        var (report, log) = await RepairAsync(Apply);

        Assert.True(report.Applied);
        Assert.Equal(1, report.Repaired);
        Assert.Empty(report.Failed);
        Assert.Empty(report.Unmatched);
        Assert.Contains($"print-image {row.ImageId}: repaired from {BlobContainers.PrintImagesArchive}/{row.ArchiveName}", log, StringComparison.Ordinal);

        var image = await LoadPrintImageAsync(row.ImageId);
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal(row.FileId, image.FileId);
        Assert.EndsWith(".png", image.File.Path, StringComparison.Ordinal);
        var stored = _blobs.Blobs[image.File.Path!];
        Assert.Equal(stored.LongLength, image.File.Size);
        using (var decoded = SKBitmap.Decode(stored))
        {
            Assert.Equal(0, decoded.GetPixel(2, 2).Alpha);
            Assert.Equal(255, decoded.GetPixel(100, 50).Alpha);
        }

        // A new social file on white; the old one's row and blob are gone.
        Assert.NotEqual(row.SocialFileId, image.SocialFileId);
        using (var social = SKBitmap.Decode(_blobs.Blobs[image.SocialFile!.Path!]))
            AssertWhite(social.GetPixel(2, 2));
        Assert.Equal(200, image.SocialWidth);
        Assert.Equal(100, image.SocialHeight);
        Assert.False(await FileRowExistsAsync(row.SocialFileId));
        Assert.False(_blobs.BlobExists(row.SocialPath));
        Assert.False(_blobs.BlobExists(row.OriginalPath));

        // The thumbnail kept its alpha all along and is left alone; the archive copy stays.
        Assert.Equal(row.ThumbnailFileId, image.ThumbnailFileId);
        Assert.Equal(uploaded, _blobs.Blobs[$"{BlobContainers.PrintImagesArchive}/{row.ArchiveName}"]);
    }

    [Fact]
    public async Task OpaqueOriginal_IsLeftAsItIs()
    {
        var printId = await CreatePrintAsync();
        var row = await SeedConvertedPrintImageAsync(printId, OpaquePngBytes());
        var uploads = _blobs.UploadedBlobNames.Count;

        var (report, log) = await RepairAsync(Apply);

        Assert.Equal(0, report.Repaired);
        Assert.Equal(1, report.Opaque);
        Assert.Contains($"print-image {row.ImageId}: opaque", log, StringComparison.Ordinal);
        var image = await LoadPrintImageAsync(row.ImageId);
        Assert.Equal("image/jpeg", image.ContentType);
        Assert.Equal(row.OriginalPath, image.File.Path);
        Assert.Equal(row.SocialFileId, image.SocialFileId);
        Assert.Equal(uploads, _blobs.UploadedBlobNames.Count);
        Assert.Empty(_blobs.DeletedBlobNames);
    }

    [Fact]
    public async Task ProjectImage_IsMatchedThroughTheBackfillLog()
    {
        var projectId = await CreateProjectAsync();
        var row = await SeedConvertedProjectImageAsync(projectId, ImageBackfillTests.TransparentPngBytes());
        var lines = new[]
        {
            "Mode: APPLY",
            $"project-image {row.ImageId}: image/png -> image/jpeg + thumbnail + social, 1234 -> 5678 bytes, original archived as {BlobContainers.ProjectImagesArchive}/{row.ArchiveName}\r",
        };
        var parsed = BackfillLog.Parse(lines);

        var (report, _) = await RepairAsync(Apply with { FromLog = parsed.Entries });

        Assert.Equal(1, report.LogEntries);
        Assert.Equal(1, report.Repaired);
        var image = await LoadProjectImageAsync(row.ImageId);
        Assert.Equal("image/png", image.ContentType);
        Assert.StartsWith($"{BlobContainers.ProjectImages}/", image.File.Path, StringComparison.Ordinal);
        Assert.EndsWith(".png", image.File.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchiveBlobWithNoLinkToARow_IsReported_AndNothingChanges()
    {
        // A project archive name is random, so without its log line nothing links it to a row.
        var projectId = await CreateProjectAsync();
        var row = await SeedConvertedProjectImageAsync(projectId, ImageBackfillTests.TransparentPngBytes());

        var (report, log) = await RepairAsync(Apply);

        var unmatched = Assert.Single(report.Unmatched);
        Assert.Equal($"{BlobContainers.ProjectImagesArchive}/{row.ArchiveName}", unmatched.ArchivePath);
        Assert.Contains(row.ArchiveName, log, StringComparison.Ordinal);
        Assert.Equal(0, report.Repaired);
        Assert.Equal("image/jpeg", (await LoadProjectImageAsync(row.ImageId)).ContentType);
    }

    [Fact]
    public async Task LogAndFileIdThatDisagree_AreAConflict_AndNeitherRowIsTouched()
    {
        var printId = await CreatePrintAsync();
        var first = await SeedConvertedPrintImageAsync(printId, ImageBackfillTests.TransparentPngBytes());
        var second = await SeedConvertedPrintImageAsync(printId, ImageBackfillTests.TransparentPngBytes(), archive: false);
        // The log claims first's archive copy belongs to the second row.
        var entries = new[] { new ArchivedOriginal(new ImageRef(ImageRef.PrintImage, second.ImageId), BlobContainers.PrintImagesArchive, first.ArchiveName) };

        var (report, _) = await RepairAsync(Apply with { FromLog = entries });

        Assert.Single(report.Conflicts);
        Assert.Equal(0, report.Repaired);
        Assert.Equal("image/jpeg", (await LoadPrintImageAsync(first.ImageId)).ContentType);
        Assert.Equal("image/jpeg", (await LoadPrintImageAsync(second.ImageId)).ContentType);
    }

    [Fact]
    public async Task ArchiveCopyOfADifferentSize_IsSkipped_AsNotTheSameImage()
    {
        var printId = await CreatePrintAsync();
        var row = await SeedConvertedPrintImageAsync(printId, ImageBackfillTests.TransparentPngBytes(), width: 640);

        var (report, _) = await RepairAsync(Apply);

        var skipped = Assert.Single(report.Skipped);
        Assert.Equal(new ImageRef(ImageRef.PrintImage, row.ImageId), skipped.Image);
        Assert.Contains("200x100", skipped.Reason, StringComparison.Ordinal);
        Assert.Equal("image/jpeg", (await LoadPrintImageAsync(row.ImageId)).ContentType);
    }

    [Fact]
    public async Task RowTheBackfillNeverConverted_IsLeftForTheBackfill()
    {
        // The run was stopped after archiving but before the save: the row is still legacy.
        var printId = await CreatePrintAsync();
        var row = await SeedConvertedPrintImageAsync(printId, ImageBackfillTests.TransparentPngBytes());
        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var image = await context.PrintImages.SingleAsync(i => i.Id == row.ImageId, Ct);
            image.ContentType = null;
            await context.SaveChangesAsync(Ct);
        }

        var (report, _) = await RepairAsync(Apply);

        Assert.Equal(1, report.NotConverted);
        Assert.Equal(0, report.Repaired);
        Assert.Null((await LoadPrintImageAsync(row.ImageId)).ContentType);
    }

    [Fact]
    public async Task ReRun_IsANoOp()
    {
        var printId = await CreatePrintAsync();
        var row = await SeedConvertedPrintImageAsync(printId, ImageBackfillTests.TransparentPngBytes());
        await RepairAsync(Apply);
        var after = await LoadPrintImageAsync(row.ImageId);
        var uploads = _blobs.UploadedBlobNames.Count;

        var (report, _) = await RepairAsync(Apply);

        Assert.Equal(0, report.Repaired);
        Assert.Equal(1, report.AlreadyRepaired);
        Assert.Equal(uploads, _blobs.UploadedBlobNames.Count);
        Assert.Equal(after.File.Path, (await LoadPrintImageAsync(row.ImageId)).File.Path);
    }

    [Fact]
    public async Task Limit_BoundsTheRowsRepaired_AndTheNextRunResumes()
    {
        var printId = await CreatePrintAsync();
        var rows = new List<Converted>();
        for (var i = 0; i < 3; i++)
            rows.Add(await SeedConvertedPrintImageAsync(printId, ImageBackfillTests.TransparentPngBytes()));

        var (first, _) = await RepairAsync(Apply with { Limit = 2 });
        var (second, _) = await RepairAsync(Apply);

        Assert.Equal(2, first.Repaired);
        Assert.Equal(1, second.Repaired);
        foreach (var row in rows)
            Assert.Equal("image/png", (await LoadPrintImageAsync(row.ImageId)).ContentType);
    }

    [Fact]
    public async Task FailedSave_LeavesTheRowUnchanged_AndRemovesTheNewBlobs()
    {
        var printId = await CreatePrintAsync();
        var row = await SeedConvertedPrintImageAsync(printId, ImageBackfillTests.TransparentPngBytes());
        var firstNewUpload = _blobs.UploadedBlobNames.Count;

        var (report, _) = await RepairAsync(Apply, interceptors: new FailingSave());

        Assert.Equal(new ImageRef(ImageRef.PrintImage, row.ImageId), Assert.Single(report.Failed).Image);
        var image = await LoadPrintImageAsync(row.ImageId);
        Assert.Equal("image/jpeg", image.ContentType);
        Assert.Equal(row.OriginalPath, image.File.Path);
        Assert.Equal(row.SocialFileId, image.SocialFileId);
        Assert.True(_blobs.BlobExists(row.OriginalPath));
        Assert.True(_blobs.BlobExists(row.SocialPath));
        var written = _blobs.UploadedBlobNames.Skip(firstNewUpload).ToList();
        Assert.NotEmpty(written);
        Assert.All(written, path => Assert.False(_blobs.BlobExists(path), $"{path} should have been removed"));
        Assert.True(_blobs.BlobExists($"{BlobContainers.PrintImagesArchive}/{row.ArchiveName}"));

        // And a later run, with the fault gone, completes it.
        Assert.Equal(1, (await RepairAsync(Apply)).Report.Repaired);
    }

    [Fact]
    public async Task ArchiveTaggedWithItsRow_IsMatchedByTheTag()
    {
        // What the backfill writes from now on: the archive copy names its row in metadata.
        var projectId = await CreateProjectAsync();
        var row = await SeedConvertedProjectImageAsync(projectId, ImageBackfillTests.TransparentPngBytes());
        var path = $"{BlobContainers.ProjectImagesArchive}/{row.ArchiveName}";
        _blobs.Metadata[path] = new Dictionary<string, string>
        {
            [ArchiveMetadata.ImageKind] = ImageRef.ProjectImage,
            [ArchiveMetadata.ImageId] = row.ImageId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        var (report, _) = await RepairAsync(Apply);

        Assert.Equal(1, report.Repaired);
        Assert.Equal("image/png", (await LoadProjectImageAsync(row.ImageId)).ContentType);
    }

    [Fact]
    public void LogParser_ReadsArchivedRows_AndIgnoresEverythingElse()
    {
        var lines = new[]
        {
            "Database: PrintLogDb",
            "print-image 12: image/png -> image/jpeg + thumbnail + social, 10 -> 20 bytes, original archived as printimages-archive/0c6a8f9e-6b8e-4a4c-9a55-5b3b4bd5e0a1.png",
            "print-image 13: would be image/png -> image/jpeg + thumbnail + social, 10 -> 20 bytes",
            "print-image 14: social only, 10 -> 20 bytes",
            "print-image 15: blob missing",
            "project-image 3: image/jpeg -> image/jpeg + thumbnail, 10 -> 20 bytes, original archived as projectimages-archive/a b.JPG",
            "print-image 16: image/png -> image/jpeg, 1 -> 2 bytes, original archived as projectimages-archive/x.png",
        };

        var parsed = BackfillLog.Parse(lines);

        Assert.Equal(
            [
                new ArchivedOriginal(new ImageRef(ImageRef.PrintImage, 12), BlobContainers.PrintImagesArchive, "0c6a8f9e-6b8e-4a4c-9a55-5b3b4bd5e0a1.png"),
                new ArchivedOriginal(new ImageRef(ImageRef.ProjectImage, 3), BlobContainers.ProjectImagesArchive, "a b.JPG"),
            ],
            parsed.Entries);
        // A print row archived in the project container is not believed.
        Assert.Contains(parsed.Rejected, line => line.StartsWith("print-image 16:", StringComparison.Ordinal));
    }

    [Fact]
    public void Summary_ListsCountsAndWhatNeedsAHuman()
    {
        var report = new TransparencyRepairReport { Applied = false };
        report.Unmatched.Add(new ArchiveIssue("printimages-archive/x.png", "no row"));
        report.Skipped.Add(new ImageIssue(new ImageRef(ImageRef.PrintImage, 12), "bad"));
        var writer = new StringWriter();

        report.WriteSummary(writer);

        var text = writer.ToString();
        Assert.Contains("DRY RUN", text, StringComparison.Ordinal);
        Assert.Contains("printimages-archive/x.png", text, StringComparison.Ordinal);
        Assert.Contains("print-image 12", text, StringComparison.Ordinal);
    }

    private sealed class FailingSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => throw new DbUpdateException("simulated database failure");
    }
}
