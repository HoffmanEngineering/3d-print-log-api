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
/// The image backfill re-processes print and project images stored before uploads were
/// processed, so they match what a new upload stores: a re-encoded JPEG original, a WebP
/// thumbnail, and the decoded type and dimensions on the row.
/// </summary>
public class ImageBackfillTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Arrange helpers ----

    private InMemoryBlobStorageService Blobs()
        => (InMemoryBlobStorageService)_factory.Services.GetRequiredService<IBlobStorageService>();

    private static byte[] Fixture(string name)
        => System.IO.File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "tests-fixtures", name));

    private static byte[] PngBytes(int width = 200, int height = 100)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
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
            Title = $"Backfill Print {Guid.NewGuid()}",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Pending,
            ViewStatus = PrintViewStatus.Public,
            AllowComments = true
        });
        var response = await _client.SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();
        var print = await response.Content.ReadFromJsonAsync<PrintDetailDTO>(Ct);
        return print!.Id;
    }

    private async Task<Guid> CreateProjectAsync()
    {
        var request = AuthenticatedRequest(HttpMethod.Post, "/api/Projects");
        request.Content = JsonContent.Create(new AddProjectDto
        {
            Name = $"Backfill Project {Guid.NewGuid()}",
            Status = Project.ProjectStatus.InProgress,
            ViewStatus = Project.ProjectViewStatus.Public
        });
        var response = await _client.SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();
        var project = await response.Content.ReadFromJsonAsync<ProjectDetailDto>(Ct);
        return project!.Id;
    }

    private async Task<long> TestUserIdAsync(PrintLogContext context)
        => await context.Users
            .Where(u => u.OAuthUserId == IntegrationTestSeeder.TestUserOAuthId)
            .Select(u => u.Id)
            .FirstAsync(Ct);

    /// <summary>
    /// A print image row as uploads stored it before processing: the raw bytes under
    /// <c>{fileId}{client extension}</c>, Path in the "container/name" form, no content type.
    /// </summary>
    private async Task<(int ImageId, string BlobName)> SeedLegacyPrintImageAsync(
        long printId, string extension, byte[] bytes, bool uploadBlob = true)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var userId = await TestUserIdAsync(context);

        var fileId = Guid.NewGuid();
        var blobName = $"{fileId}{extension}";
        if (uploadBlob)
        {
            using var stream = new MemoryStream(bytes);
            await Blobs().UploadAsync(BlobContainers.PrintImages, blobName, stream);
        }

        var file = new Models.File
        {
            Id = fileId,
            Path = $"{BlobContainers.PrintImages}/{blobName}",
            Size = bytes.Length,
            CreatedById = userId,
            UpdatedById = userId
        };
        context.Files.Add(file);
        var image = new PrintImage { PrintId = printId, File = file, CreatedById = userId, UpdatedById = userId };
        context.PrintImages.Add(image);
        await context.SaveChangesAsync(Ct);
        return (image.Id, blobName);
    }

    /// <summary>
    /// A project image row as uploads stored it before processing: a random blob name with the
    /// client's extension, and Path holding the bare blob name.
    /// </summary>
    private async Task<(int ImageId, string BlobName)> SeedLegacyProjectImageAsync(
        Guid projectId, string extension, byte[] bytes)
    {
        var blobName = $"{Guid.NewGuid()}{extension}";
        using (var stream = new MemoryStream(bytes))
            await Blobs().UploadAsync(BlobContainers.ProjectImages, blobName, stream);

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var userId = await TestUserIdAsync(context);

        var file = new Models.File { Path = blobName, Size = bytes.Length, CreatedById = userId, UpdatedById = userId };
        context.Files.Add(file);
        var image = new ProjectImage { ProjectId = projectId, File = file, CreatedById = userId, UpdatedById = userId };
        context.ProjectImages.Add(image);
        await context.SaveChangesAsync(Ct);
        return (image.Id, blobName);
    }

    private async Task<PrintImage> LoadPrintImageAsync(int imageId)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await context.PrintImages
            .Include(pi => pi.File)
            .Include(pi => pi.ThumbnailFile)
            .Include(pi => pi.SocialFile)
            .AsNoTracking()
            .SingleAsync(pi => pi.Id == imageId, Ct);
    }

    private async Task<ProjectImage> LoadProjectImageAsync(int imageId)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await context.ProjectImages
            .Include(pi => pi.File)
            .Include(pi => pi.ThumbnailFile)
            .Include(pi => pi.SocialFile)
            .AsNoTracking()
            .SingleAsync(pi => pi.Id == imageId, Ct);
    }

    /// <summary>Runs the backfill over its own context on the shared connection.</summary>
    private async Task<ImageBackfillReport> RunAsync(
        ImageBackfillOptions options, IBlobStorageService? blobs = null, params IInterceptor[] interceptors)
    {
        using var scope = _factory.Services.CreateScope();
        var dbOptions = new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlite(scope.ServiceProvider.GetRequiredService<PrintLogContext>().Database.GetDbConnection())
            .AddInterceptors(interceptors)
            .Options;
        await using var context = new PrintLogContext(dbOptions);
        blobs ??= Blobs();
        var runner = new ImageBackfillRunner(
            context, blobs, new ImageProcessingService(), new ProcessedImageWriter(context, blobs), TextWriter.Null);
        return await runner.RunAsync(options, Ct);
    }

    private static ImageBackfillOptions ApplyTo(long printId) => new() { Apply = true, PrintId = printId };

    // ---- Tests ----

    [Fact]
    public async Task DryRun_IsTheDefault_AndChangesNothing()
    {
        var printId = await CreatePrintAsync();
        var (imageId, blobName) = await SeedLegacyPrintImageAsync(printId, ".jpg", Fixture("exif-orientation-6.jpg"));
        var uploadsBefore = Blobs().UploadedBlobNames.Count;
        var deletesBefore = Blobs().DeletedBlobNames.Count;

        var report = await RunAsync(new ImageBackfillOptions { PrintId = printId });

        Assert.False(report.Applied);
        Assert.Equal(1, report.Processed);
        Assert.True(report.BytesAfter > 0);
        var image = await LoadPrintImageAsync(imageId);
        Assert.Null(image.ContentType);
        Assert.Null(image.ThumbnailFileId);
        Assert.Equal($"{BlobContainers.PrintImages}/{blobName}", image.File.Path);
        Assert.Equal(uploadsBefore, Blobs().UploadedBlobNames.Count);
        Assert.Equal(deletesBefore, Blobs().DeletedBlobNames.Count);
    }

    [Fact]
    public async Task Apply_ReEncodesALegacyJpeg_AndAddsAThumbnail()
    {
        var printId = await CreatePrintAsync();
        var original = Fixture("exif-orientation-6.jpg");
        var (imageId, blobName) = await SeedLegacyPrintImageAsync(printId, ".jpg", original);
        var before = await LoadPrintImageAsync(imageId);

        var report = await RunAsync(ApplyTo(printId));

        Assert.True(report.Applied);
        Assert.Equal(1, report.Processed);
        Assert.Empty(report.Failed);
        var image = await LoadPrintImageAsync(imageId);
        Assert.Equal("image/jpeg", image.ContentType);
        // The fixture is stored 100x50 with orientation 6, so upright it is 50x100.
        Assert.Equal(50, image.Width);
        Assert.Equal(100, image.Height);

        // Same File row, pointed at a new canonical blob.
        Assert.Equal(before.FileId, image.FileId);
        Assert.StartsWith($"{BlobContainers.PrintImages}/", image.File.Path, StringComparison.Ordinal);
        Assert.EndsWith(".jpg", image.File.Path, StringComparison.Ordinal);
        Assert.NotEqual(before.File.Path, image.File.Path);
        var stored = Blobs().Blobs[image.File.Path!];
        Assert.Equal(image.File.Size, stored.LongLength);
        Assert.DoesNotContain("Exif", System.Text.Encoding.ASCII.GetString(stored), StringComparison.Ordinal);

        Assert.NotNull(image.ThumbnailFile);
        Assert.EndsWith(".webp", image.ThumbnailFile!.Path, StringComparison.Ordinal);
        Assert.True(Blobs().BlobExists(image.ThumbnailFile.Path!));

        Assert.NotNull(image.SocialFile);
        Assert.EndsWith(".jpg", image.SocialFile!.Path, StringComparison.Ordinal);
        Assert.True(Blobs().BlobExists(image.SocialFile.Path!));
        Assert.Equal(50, image.SocialWidth);
        Assert.Equal(100, image.SocialHeight);

        // The untouched bytes are archived, and the old blob is gone.
        Assert.Equal(original, Blobs().Blobs[$"{BlobContainers.PrintImagesArchive}/{blobName}"]);
        Assert.False(Blobs().BlobExists($"{BlobContainers.PrintImages}/{blobName}"));
        Assert.Equal(original.LongLength, report.BytesBefore);
        Assert.Equal(
            stored.LongLength + Blobs().Blobs[image.ThumbnailFile.Path!].LongLength
                + Blobs().Blobs[image.SocialFile.Path!].LongLength,
            report.BytesAfter);
    }

    [Fact]
    public async Task Apply_ArchivesTheOriginal_BeforeWritingAnythingElse()
    {
        var printId = await CreatePrintAsync();
        var (_, blobName) = await SeedLegacyPrintImageAsync(printId, ".png", PngBytes());
        var firstNewUpload = Blobs().UploadedBlobNames.Count;

        await RunAsync(ApplyTo(printId));

        var uploads = Blobs().UploadedBlobNames.Skip(firstNewUpload).ToList();
        Assert.Equal(4, uploads.Count); // archive, original, thumbnail, social
        Assert.Equal($"{BlobContainers.PrintImagesArchive}/{blobName}", uploads[0]);
    }

    [Fact]
    public async Task Apply_DeletesTheOldBlob_OnlyAfterTheRowIsCommitted()
    {
        var printId = await CreatePrintAsync();
        var (imageId, blobName) = await SeedLegacyPrintImageAsync(printId, ".png", PngBytes());
        string? committedTypeAtDelete = "not deleted";
        var blobs = new ScriptedBlobStorage(Blobs())
        {
            BeforeDelete = async (container, name) =>
            {
                if (container == BlobContainers.PrintImages && name == blobName)
                    committedTypeAtDelete = (await LoadPrintImageAsync(imageId)).ContentType;
            }
        };

        await RunAsync(ApplyTo(printId), blobs);

        Assert.Equal("image/jpeg", committedTypeAtDelete);
        Assert.False(Blobs().BlobExists($"{BlobContainers.PrintImages}/{blobName}"));
    }

    [Theory]
    [InlineData(".png", "png")]
    [InlineData(".gif", "gif")]
    [InlineData(".bmp", "bmp")]
    public async Task Apply_ConvertsOtherLegacyFormats_ToTheCanonicalJpeg(string extension, string kind)
    {
        var bytes = kind switch
        {
            "png" => PngBytes(),
            "gif" => Fixture("legacy-animated.gif"),
            _ => Fixture("legacy.bmp"),
        };
        var printId = await CreatePrintAsync();
        var (imageId, _) = await SeedLegacyPrintImageAsync(printId, extension, bytes);

        var report = await RunAsync(ApplyTo(printId));

        Assert.Equal(1, report.Processed);
        Assert.Equal(kind is "gif" or "bmp" ? 1 : 0, report.ConvertedFromLegacyFormat);
        var image = await LoadPrintImageAsync(imageId);
        Assert.Equal("image/jpeg", image.ContentType);
        Assert.EndsWith(".jpg", image.File.Path, StringComparison.Ordinal);
        using var codec = SKCodec.Create(new MemoryStream(Blobs().Blobs[image.File.Path!]));
        Assert.Equal(SKEncodedImageFormat.Jpeg, codec.EncodedFormat);
        Assert.NotNull(image.ThumbnailFileId);
    }

    [Fact]
    public async Task UndecodableImage_IsSkippedAndReported_AndLeftUntouched()
    {
        var printId = await CreatePrintAsync();
        var junk = "not an image at all"u8.ToArray();
        var (imageId, blobName) = await SeedLegacyPrintImageAsync(printId, ".jpg", junk);

        var report = await RunAsync(ApplyTo(printId));

        Assert.Equal(0, report.Processed);
        var skipped = Assert.Single(report.Undecodable);
        Assert.Equal(new ImageRef(ImageRef.PrintImage, imageId), skipped.Image);
        var image = await LoadPrintImageAsync(imageId);
        Assert.Null(image.ContentType);
        Assert.Equal($"{BlobContainers.PrintImages}/{blobName}", image.File.Path);
        Assert.Equal(junk, Blobs().Blobs[$"{BlobContainers.PrintImages}/{blobName}"]);
        Assert.False(Blobs().BlobExists($"{BlobContainers.PrintImagesArchive}/{blobName}"));
    }

    [Fact]
    public async Task MissingBlob_IsReported_AndLeftUntouched()
    {
        var printId = await CreatePrintAsync();
        var (imageId, _) = await SeedLegacyPrintImageAsync(printId, ".jpg", PngBytes(), uploadBlob: false);

        var report = await RunAsync(ApplyTo(printId));

        Assert.Equal(new ImageRef(ImageRef.PrintImage, imageId), Assert.Single(report.Missing).Image);
        Assert.Null((await LoadPrintImageAsync(imageId)).ContentType);
    }

    [Fact]
    public async Task ReRun_IsANoOp()
    {
        var printId = await CreatePrintAsync();
        var (imageId, _) = await SeedLegacyPrintImageAsync(printId, ".png", PngBytes());
        await RunAsync(ApplyTo(printId));
        var afterFirst = await LoadPrintImageAsync(imageId);
        var uploads = Blobs().UploadedBlobNames.Count;

        var report = await RunAsync(ApplyTo(printId));

        Assert.Equal(0, report.Examined);
        Assert.Equal(uploads, Blobs().UploadedBlobNames.Count);
        var afterSecond = await LoadPrintImageAsync(imageId);
        Assert.Equal(afterFirst.File.Path, afterSecond.File.Path);
        Assert.Equal(afterFirst.ThumbnailFileId, afterSecond.ThumbnailFileId);
    }

    [Fact]
    public async Task ProjectImage_WithABareBlobName_IsConverted()
    {
        var projectId = await CreateProjectAsync();
        var (imageId, blobName) = await SeedLegacyProjectImageAsync(projectId, ".png", PngBytes());

        var report = await RunAsync(new ImageBackfillOptions { Apply = true, ProjectId = projectId });

        Assert.Equal(1, report.Processed);
        var image = await LoadProjectImageAsync(imageId);
        Assert.Equal("image/jpeg", image.ContentType);
        Assert.StartsWith($"{BlobContainers.ProjectImages}/", image.File.Path, StringComparison.Ordinal);
        Assert.True(Blobs().BlobExists(image.File.Path!));
        Assert.NotNull(image.ThumbnailFile);
        Assert.True(Blobs().BlobExists($"{BlobContainers.ProjectImagesArchive}/{blobName}"));
        Assert.False(Blobs().BlobExists($"{BlobContainers.ProjectImages}/{blobName}"));
    }

    [Fact]
    public async Task PrintFilter_LeavesProjectImagesAlone()
    {
        var printId = await CreatePrintAsync();
        var projectId = await CreateProjectAsync();
        await SeedLegacyPrintImageAsync(printId, ".png", PngBytes());
        var (projectImageId, _) = await SeedLegacyProjectImageAsync(projectId, ".png", PngBytes());

        await RunAsync(ApplyTo(printId));

        Assert.Null((await LoadProjectImageAsync(projectImageId)).ContentType);
    }

    [Fact]
    public async Task FailedThumbnailUpload_LeavesTheRowUntouched_AndRemovesNewBlobs()
    {
        var printId = await CreatePrintAsync();
        var (imageId, blobName) = await SeedLegacyPrintImageAsync(printId, ".png", PngBytes());
        var firstNewUpload = Blobs().UploadedBlobNames.Count;
        var blobs = new ScriptedBlobStorage(Blobs())
        {
            BeforeUpload = (_, name) => name.EndsWith(".webp", StringComparison.Ordinal)
                ? throw new IOException("simulated storage failure")
                : Task.CompletedTask
        };

        var report = await RunAsync(ApplyTo(printId), blobs);

        Assert.Equal(new ImageRef(ImageRef.PrintImage, imageId), Assert.Single(report.Failed).Image);
        AssertUntouched(await LoadPrintImageAsync(imageId), blobName);
        AssertOnlyTheArchiveRemains(firstNewUpload, blobName);
    }

    [Fact]
    public async Task FailedSave_LeavesTheRowUntouched_AndRemovesNewBlobs()
    {
        var printId = await CreatePrintAsync();
        var (imageId, blobName) = await SeedLegacyPrintImageAsync(printId, ".png", PngBytes());
        var firstNewUpload = Blobs().UploadedBlobNames.Count;

        var report = await RunAsync(ApplyTo(printId), interceptors: new FailingSave());

        Assert.Equal(new ImageRef(ImageRef.PrintImage, imageId), Assert.Single(report.Failed).Image);
        AssertUntouched(await LoadPrintImageAsync(imageId), blobName);
        AssertOnlyTheArchiveRemains(firstNewUpload, blobName);

        // And a later run, with the fault gone, completes it.
        var retry = await RunAsync(ApplyTo(printId));
        Assert.Equal(1, retry.Processed);
        Assert.Equal("image/jpeg", (await LoadPrintImageAsync(imageId)).ContentType);
    }

    [Fact]
    public async Task SaveThatThrowsAfterCommitting_KeepsTheNewBlobs()
    {
        // A lost acknowledgement: the row now points at the new blobs, so deleting them as a
        // failed attempt's leftovers would break it.
        var printId = await CreatePrintAsync();
        var (imageId, blobName) = await SeedLegacyPrintImageAsync(printId, ".png", PngBytes());

        var report = await RunAsync(ApplyTo(printId), interceptors: new ThrowAfterCommit());

        Assert.Empty(report.Failed);
        Assert.Equal(1, report.Processed);
        var image = await LoadPrintImageAsync(imageId);
        Assert.Equal("image/jpeg", image.ContentType);
        Assert.True(Blobs().BlobExists(image.File.Path!));
        Assert.True(Blobs().BlobExists(image.ThumbnailFile!.Path!));
        Assert.False(Blobs().BlobExists($"{BlobContainers.PrintImages}/{blobName}"));
    }

    private void AssertUntouched(PrintImage image, string blobName)
    {
        Assert.Null(image.ContentType);
        Assert.Null(image.ThumbnailFileId);
        Assert.Null(image.SocialFileId);
        Assert.Null(image.Width);
        Assert.Equal($"{BlobContainers.PrintImages}/{blobName}", image.File.Path);
        Assert.True(Blobs().BlobExists($"{BlobContainers.PrintImages}/{blobName}"));
    }

    /// <summary>
    /// Every blob the failed row wrote under the live container is gone again. The archive copy
    /// stays: it is a copy of bytes still in place, and the next run overwrites it.
    /// </summary>
    private void AssertOnlyTheArchiveRemains(int firstNewUpload, string blobName)
    {
        var written = Blobs().UploadedBlobNames.Skip(firstNewUpload).ToList();
        Assert.Contains(written, n => n.StartsWith($"{BlobContainers.PrintImages}/", StringComparison.Ordinal));
        foreach (var path in written.Where(n => n.StartsWith($"{BlobContainers.PrintImages}/", StringComparison.Ordinal)))
            Assert.False(Blobs().BlobExists(path), $"{path} should have been removed");
        Assert.True(Blobs().BlobExists($"{BlobContainers.PrintImagesArchive}/{blobName}"));
    }

    [Fact]
    public async Task Limit_AndBatchSize_BoundARun_AndTheNextRunResumes()
    {
        var printId = await CreatePrintAsync();
        var ids = new List<int>();
        for (var i = 0; i < 3; i++)
            ids.Add((await SeedLegacyPrintImageAsync(printId, ".png", PngBytes())).ImageId);

        var first = await RunAsync(new ImageBackfillOptions { Apply = true, PrintId = printId, BatchSize = 1, Limit = 2 });

        Assert.Equal(2, first.Processed);
        Assert.Equal("image/jpeg", (await LoadPrintImageAsync(ids[0])).ContentType);
        Assert.Equal("image/jpeg", (await LoadPrintImageAsync(ids[1])).ContentType);
        Assert.Null((await LoadPrintImageAsync(ids[2])).ContentType);

        var second = await RunAsync(new ImageBackfillOptions { Apply = true, PrintId = printId, BatchSize = 1 });

        Assert.Equal(1, second.Processed);
        Assert.Equal("image/jpeg", (await LoadPrintImageAsync(ids[2])).ContentType);
    }

    [Fact]
    public async Task ProcessedRowWithoutAThumbnail_GetsOnlyAThumbnail()
    {
        // A row from a processed upload whose thumbnail failed: its original is already
        // canonical, so it is neither re-encoded nor archived.
        var printId = await CreatePrintAsync();
        var (imageId, blobName) = await SeedLegacyPrintImageAsync(printId, ".jpg", Fixture("exif-orientation-6.jpg"));
        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var row = await context.PrintImages.SingleAsync(pi => pi.Id == imageId, Ct);
            row.ContentType = "image/jpeg";
            row.Width = 100;
            row.Height = 50;
            await context.SaveChangesAsync(Ct);
        }

        var report = await RunAsync(ApplyTo(printId));

        Assert.Equal(1, report.Processed);
        var image = await LoadPrintImageAsync(imageId);
        Assert.Equal($"{BlobContainers.PrintImages}/{blobName}", image.File.Path);
        Assert.True(Blobs().BlobExists(image.File.Path!));
        Assert.NotNull(image.ThumbnailFile);
        Assert.True(Blobs().BlobExists(image.ThumbnailFile!.Path!));
        Assert.NotNull(image.SocialFile);
        Assert.False(Blobs().BlobExists($"{BlobContainers.PrintImagesArchive}/{blobName}"));
    }

    [Fact]
    public async Task ProcessedRowWithAThumbnail_GetsOnlyASocialImage()
    {
        // Rows uploaded after uploads were processed but before the social derivative existed:
        // canonical original and thumbnail, no social file. Only the social file is added.
        var printId = await CreatePrintAsync();
        var (imageId, blobName) = await SeedLegacyPrintImageAsync(printId, ".jpg", Fixture("exif-orientation-6.jpg"));
        Guid thumbnailId;
        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var row = await context.PrintImages.SingleAsync(pi => pi.Id == imageId, Ct);
            var thumbnail = new Models.File
            {
                Id = Guid.NewGuid(),
                Path = $"{BlobContainers.PrintImages}/{Guid.NewGuid()}.webp",
                Size = 1,
                CreatedById = row.CreatedById,
                UpdatedById = row.CreatedById,
            };
            context.Files.Add(thumbnail);
            row.ThumbnailFile = thumbnail;
            row.ContentType = "image/jpeg";
            row.Width = 50;
            row.Height = 100;
            await context.SaveChangesAsync(Ct);
            thumbnailId = thumbnail.Id;
        }
        var firstNewUpload = Blobs().UploadedBlobNames.Count;

        var report = await RunAsync(ApplyTo(printId));

        Assert.Equal(1, report.Processed);
        Assert.Equal(1, report.DerivativesOnly);
        var image = await LoadPrintImageAsync(imageId);
        Assert.Equal($"{BlobContainers.PrintImages}/{blobName}", image.File.Path);
        Assert.Equal(thumbnailId, image.ThumbnailFileId);
        Assert.NotNull(image.SocialFile);
        Assert.Equal(50, image.SocialWidth);
        Assert.Equal(100, image.SocialHeight);
        var social = Blobs().Blobs[image.SocialFile!.Path!];
        using var codec = SKCodec.Create(new MemoryStream(social));
        Assert.Equal(SKEncodedImageFormat.Jpeg, codec.EncodedFormat);
        Assert.Equal(image.SocialFile.Path, Assert.Single(Blobs().UploadedBlobNames.Skip(firstNewUpload)));

        // And the row is now complete, so a re-run leaves it alone.
        Assert.Equal(0, (await RunAsync(ApplyTo(printId))).Examined);
    }

    [Fact]
    public void Summary_ListsCountsAndIds()
    {
        var report = new ImageBackfillReport { Applied = false };
        report.Undecodable.Add(new ImageIssue(new ImageRef(ImageRef.PrintImage, 12), "bad"));
        report.Failed.Add(new ImageIssue(new ImageRef(ImageRef.ProjectImage, 7), "boom"));
        var writer = new StringWriter();

        report.WriteSummary(writer);

        var text = writer.ToString();
        Assert.Contains("DRY RUN", text, StringComparison.Ordinal);
        Assert.Contains("print-image 12", text, StringComparison.Ordinal);
        Assert.Contains("project-image 7", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The queries reach the columns through <see cref="IStoredImage"/>, which SQLite accepting
    /// does not prove SQL Server will. Compiled through the SQL Server provider; no database.
    /// </summary>
    [Fact]
    public void Queries_TranslateForSqlServer()
    {
        using var context = new PrintLogContext(new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlServer("Server=unused;Database=unused;Trusted_Connection=True;")
            .Options);

        var batch = ImageBackfillSelection.NextBatch(context.PrintImages.Where(i => i.PrintId == 5), 10, 50).ToQueryString();
        var row = ImageBackfillSelection.Row<ProjectImage>(context.ProjectImages, 3).ToQueryString();

        Assert.Contains("[ContentType] IS NULL", batch, StringComparison.Ordinal);
        Assert.Contains("[ThumbnailFileId] IS NULL", batch, StringComparison.Ordinal);
        Assert.Contains("[SocialFileId] IS NULL", batch, StringComparison.Ordinal);
        Assert.Contains("[Files]", row, StringComparison.Ordinal);
    }

    // ---- Doubles ----

    /// <summary>Forwards to the shared in-memory store, with hooks to observe or fail a call.</summary>
    private sealed class ScriptedBlobStorage(InMemoryBlobStorageService inner) : IBlobStorageService
    {
        public Func<string, string, Task>? BeforeUpload { get; init; }
        public Func<string, string, Task>? BeforeDelete { get; init; }

        public async Task<BlobUploadResult> UploadAsync(string containerName, string blobName, Stream stream)
        {
            if (BeforeUpload is not null) await BeforeUpload(containerName, blobName);
            return await inner.UploadAsync(containerName, blobName, stream);
        }

        public async Task DeleteBlobAsync(string containerName, string blobName)
        {
            if (BeforeDelete is not null) await BeforeDelete(containerName, blobName);
            await inner.DeleteBlobAsync(containerName, blobName);
        }

        public Task<(Stream stream, string fileName)?> DownloadAsync(string containerName, string blobName)
            => inner.DownloadAsync(containerName, blobName);

        public Task<BlobContent?> OpenReadAsync(string containerName, string blobName, bool seekable, CancellationToken cancellationToken = default)
            => inner.OpenReadAsync(containerName, blobName, seekable, cancellationToken);

        public Task<Uri> GenerateSasUploadUrlAsync(string containerName, string blobName, TimeSpan expiry)
            => inner.GenerateSasUploadUrlAsync(containerName, blobName, expiry);

        public Task<Uri> GenerateSasDownloadUrlAsync(string containerName, string blobName, string contentType, string originalFileName, TimeSpan expiry)
            => inner.GenerateSasDownloadUrlAsync(containerName, blobName, contentType, originalFileName, expiry);

        public Task<Uri> GenerateSasInlineUrlAsync(string containerName, string blobName, string contentType, TimeSpan bucketSize, TimeSpan cacheControlMaxAge)
            => inner.GenerateSasInlineUrlAsync(containerName, blobName, contentType, bucketSize, cacheControlMaxAge);
    }

    private sealed class FailingSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => throw new DbUpdateException("simulated database failure");
    }

    private sealed class ThrowAfterCommit : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
            => throw new DbUpdateException("simulated lost acknowledgement");
    }
}
