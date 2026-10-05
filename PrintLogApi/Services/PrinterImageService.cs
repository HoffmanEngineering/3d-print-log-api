using Microsoft.EntityFrameworkCore;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;

namespace PrintLogApi.Services;

/// <inheritdoc cref="IPrinterImageService"/>
public class PrinterImageService(
    PrintLogContext context,
    IBlobStorageService blobStorage,
    IImageProcessingService imageProcessing,
    IMediaStorageQuotaService quota,
    IPrinterService printerService) : IPrinterImageService
{
    public async Task<PrinterImage> AddImageAsync(
        long printerId, Stream content, long userId, CancellationToken ct = default)
    {
        _ = await context.Printers
                .FirstOrDefaultAsync(p => p.Id == printerId && p.UserId == userId, ct)
            ?? throw new DoesNotExistException();

        var maxImages = await printerService.GetMaxImagesPerPrinter(userId);

        // Decode BEFORE touching storage: an invalid upload must not create blobs.
        var processed = await imageProcessing.ProcessAsync(content, ct);

        // Account-wide byte quota. A per-printer cap alone bounds nothing, because nothing
        // caps how many printers a user creates.
        var newBytes = processed.Original.LongLength + (processed.Thumbnail?.LongLength ?? 0);
        await quota.EnsureCapacityAsync(userId, newBytes, ct);

        // SqlServerRetryingExecutionStrategy forbids user-initiated transactions unless they run
        // inside an execution strategy, so the whole tx is the retriable unit. The integration
        // suite runs on SQLite, whose strategy permits them, so omitting this fails only against
        // SQL Server - which is every real environment.
        var strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(ct);

            // Blobs are written before the transaction commits and are not covered by its
            // rollback, so anything already uploaded has to be cleaned up by hand if the
            // database work then fails.
            //
            // Declared per attempt, so a retried attempt cleans up only the blobs it wrote.
            var uploadedBlobNames = new List<string>();

            try
            {
                var existingCount = await context.PrinterImages
                    .CountAsync(pi => pi.PrinterId == printerId, ct);

                if (existingCount >= maxImages)
                    throw new ArgumentException($"Maximum of {maxImages} images per printer allowed");

                var originalFile = await UploadAndRecordAsync(
                    processed.Original, ".jpg", userId, uploadedBlobNames, ct);
                var thumbnailFile = processed.Thumbnail is null
                    ? null
                    : await UploadAndRecordAsync(
                        processed.Thumbnail, ".webp", userId, uploadedBlobNames, ct);

                await context.SaveChangesAsync(ct);

                var image = new PrinterImage
                {
                    PrinterId = printerId,
                    FileId = originalFile.Id,
                    ThumbnailFileId = thumbnailFile?.Id,
                    ContentType = processed.ContentType,
                    IsDefault = existingCount == 0,
                    DisplayOrder = existingCount,
                    CreatedById = userId,
                    UpdatedById = userId
                };
                context.PrinterImages.Add(image);

                try
                {
                    await context.SaveChangesAsync(ct);
                }
                catch (DbUpdateException) when (image.IsDefault)
                {
                    // A transaction at the default isolation level does NOT serialize the two
                    // CountAsync calls, so two concurrent first uploads can both read zero. The
                    // filtered unique index rejects the loser; demote and retry once rather
                    // than surfacing a 500.
                    image.IsDefault = false;
                    image.DisplayOrder = await context.PrinterImages
                        .CountAsync(pi => pi.PrinterId == printerId, ct);
                    await context.SaveChangesAsync(ct);
                }

                await transaction.CommitAsync(ct);
                return image;
            }
            catch
            {
                await DeleteUploadedBlobsAsync(uploadedBlobNames);
                throw;
            }
        });
    }

    public async Task DeleteImageAsync(
        long printerId, int imageId, long userId, CancellationToken ct = default)
    {
        // Ownership is Printer.UserId. PrinterImage.CreatedById records the uploader, so a
        // predicate on it compiles, reads plausibly, and lets a caller delete an image on a
        // printer they do not own.
        var image = await context.PrinterImages
            .Include(pi => pi.File)
            .Include(pi => pi.ThumbnailFile)
            .FirstOrDefaultAsync(pi => pi.PrinterId == printerId
                                    && pi.Id == imageId
                                    && pi.Printer.UserId == userId, ct)
            ?? throw new DoesNotExistException();

        var blobNames = new[] { image.File?.Path, image.ThumbnailFile?.Path }
            .Where(p => p is not null)
            .Select(p => Path.GetFileName(p)!)
            .ToList();

        var wasDefault = image.IsDefault;

        // Removal and promotion are two saves, because the filtered unique index would
        // reject an intermediate state with two defaults. One transaction around both keeps
        // them atomic anyway: without it, a failure in between leaves a non-empty printer
        // with no default at all.
        // See AddImageAsync: the retrying execution strategy owns the transaction.
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(ct);

            context.PrinterImages.Remove(image);
            if (image.File is not null) context.Files.Remove(image.File);
            if (image.ThumbnailFile is not null) context.Files.Remove(image.ThumbnailFile);

            await context.SaveChangesAsync(ct);

            if (wasDefault)
            {
                // Read after the delete is applied, so the old default is already gone.
                var next = await context.PrinterImages
                    .Where(pi => pi.PrinterId == printerId)
                    .OrderBy(pi => pi.DisplayOrder).ThenBy(pi => pi.Id)
                    .FirstOrDefaultAsync(ct);
                if (next is not null)
                {
                    next.IsDefault = true;
                    await context.SaveChangesAsync(ct);
                }
            }

            await transaction.CommitAsync(ct);
        });

        // Blobs LAST. Deleting them first means a failed SaveChangesAsync leaves a row
        // pointing at destroyed bytes. This ordering fails toward an orphaned blob (wasted
        // storage) instead of a broken record.
        foreach (var name in blobNames)
            await blobStorage.DeleteBlobAsync(BlobContainers.PrinterImages, name);
    }

    public async Task ReorderImagesAsync(
        long printerId, IList<int> orderedImageIds, long userId, CancellationToken ct = default)
    {
        var images = await context.PrinterImages
            .Where(pi => pi.PrinterId == printerId && pi.Printer.UserId == userId)
            .ToListAsync(ct);

        // Exact-set validation, matching the print and filament endpoints. ProjectService
        // silently ignores unknown IDs and accepts partial lists, which hides client bugs.
        var supplied = orderedImageIds?.ToList() ?? [];
        if (supplied.Count == 0
            || supplied.Count != supplied.Distinct().Count()
            || supplied.Count != images.Count
            || !supplied.OrderBy(i => i).SequenceEqual(images.Select(i => i.Id).OrderBy(i => i)))
        {
            throw new ArgumentException("Image IDs do not match printer images");
        }

        for (var i = 0; i < supplied.Count; i++)
            images.First(im => im.Id == supplied[i]).DisplayOrder = i;

        await context.SaveChangesAsync(ct);
    }

    public async Task SetDefaultImageAsync(
        long printerId, int imageId, long userId, CancellationToken ct = default)
    {
        var owns = await context.PrinterImages.AnyAsync(
            pi => pi.PrinterId == printerId && pi.Id == imageId
               && pi.Printer.UserId == userId, ct);
        if (!owns) throw new DoesNotExistException();

        // One statement, so the filtered unique index never observes two defaults.
        // Clearing and setting via separate tracked updates in a single SaveChangesAsync
        // is NOT safe here: EF does not guarantee the clear is emitted first.
        await context.PrinterImages
            .Where(pi => pi.PrinterId == printerId)
            .ExecuteUpdateAsync(s => s.SetProperty(pi => pi.IsDefault, pi => pi.Id == imageId), ct);

        // ExecuteUpdateAsync goes straight to the database and does not notify the change
        // tracker, so anything already loaded in this scope still carries the old flag and
        // a re-read in the same request would serve it. Reconcile the tracked copies.
        // Materialized before the loop: writing to a tracked entity mutates the change
        // tracker, and enumerating Entries() lazily while doing so throws.
        var tracked = context.ChangeTracker.Entries<PrinterImage>()
            .Where(e => e.Entity.PrinterId == printerId)
            .ToList();

        foreach (var entry in tracked)
        {
            var isDefault = entry.Entity.Id == imageId;
            var property = entry.Property(pi => pi.IsDefault);

            // OriginalValue must move with CurrentValue. Setting IsModified = false alone
            // resets the current value BACK to the original, silently undoing the fix.
            property.OriginalValue = isDefault;
            property.CurrentValue = isDefault;
            property.IsModified = false;
        }
    }

    /// <summary>
    /// Uploads processed bytes and adds the backing <see cref="Models.File"/> row. The row is
    /// added but NOT saved: the caller saves it inside the same transaction as the image row.
    /// </summary>
    private async Task<Models.File> UploadAndRecordAsync(
        byte[] bytes, string extension, long userId, List<string> uploadedBlobNames, CancellationToken ct)
    {
        var blobName = $"{Guid.NewGuid()}{extension}";
        using var stream = new MemoryStream(bytes);
        await blobStorage.UploadAsync(BlobContainers.PrinterImages, blobName, stream);

        // Recorded only after the upload succeeds, so the compensating delete never targets
        // a blob that was never written.
        uploadedBlobNames.Add(blobName);

        var file = new Models.File
        {
            Path = blobName,
            Size = bytes.LongLength,
            CreatedById = userId,
            UpdatedById = userId
        };
        context.Files.Add(file);
        return file;
    }

    /// <summary>
    /// Best-effort cleanup of blobs written for an upload that then failed. Deliberately
    /// swallows its own failures and takes no cancellation token: it runs on the way out of a
    /// failed request, frequently a cancelled one, and a throw here would replace the real
    /// exception with a less useful one.
    /// </summary>
    private async Task DeleteUploadedBlobsAsync(List<string> blobNames)
    {
        foreach (var name in blobNames)
        {
            try
            {
                await blobStorage.DeleteBlobAsync(BlobContainers.PrinterImages, name);
            }
            catch
            {
                // Intentionally ignored - see the summary above.
            }
        }
    }
}
