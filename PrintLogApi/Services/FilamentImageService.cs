using Microsoft.EntityFrameworkCore;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;

namespace PrintLogApi.Services;

public class FilamentImageService(
    PrintLogContext context,
    IBlobStorageService blobStorage,
    IImageProcessingService imageProcessing,
    IFilamentService filamentService) : IFilamentImageService
{
    public async Task<FilamentImage> AddImageAsync(
        Guid filamentId, Stream content, long userId, CancellationToken ct = default)
    {
        _ = await context.Filaments
                .FirstOrDefaultAsync(f => f.Id == filamentId && f.CreatedById == userId, ct)
            ?? throw new DoesNotExistException();

        var maxImages = await filamentService.GetMaxImagesPerFilament(userId);

        // Decode BEFORE touching storage: an invalid upload must not create blobs.
        var processed = await imageProcessing.ProcessAsync(content, ct);

        // Account-wide byte quota, matching FileAttachmentService. A per-filament cap
        // alone bounds nothing, because nothing caps how many filaments a user creates.
        var newBytes = processed.Original.LongLength + (processed.Thumbnail?.LongLength ?? 0);
        await EnsureAccountStorageQuotaAsync(userId, newBytes, ct);

        await using var transaction = await context.Database.BeginTransactionAsync(ct);

        var existingCount = await context.FilamentImages
            .CountAsync(fi => fi.FilamentId == filamentId, ct);

        if (existingCount >= maxImages)
            throw new ArgumentException($"Maximum of {maxImages} images per filament allowed");

        var originalFile = await UploadAndRecordAsync(processed.Original, ".jpg", userId, ct);
        var thumbnailFile = processed.Thumbnail is null
            ? null
            : await UploadAndRecordAsync(processed.Thumbnail, ".webp", userId, ct);

        await context.SaveChangesAsync(ct);

        var image = new FilamentImage
        {
            FilamentId = filamentId,
            FileId = originalFile.Id,
            ThumbnailFileId = thumbnailFile?.Id,
            ContentType = processed.ContentType,
            IsDefault = existingCount == 0,
            DisplayOrder = existingCount,
            CreatedById = userId,
            UpdatedById = userId
        };
        context.FilamentImages.Add(image);

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
            image.DisplayOrder = await context.FilamentImages
                .CountAsync(fi => fi.FilamentId == filamentId, ct);
            await context.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return image;
    }

    public async Task DeleteImageAsync(
        Guid filamentId, int imageId, long userId, CancellationToken ct = default)
    {
        var image = await context.FilamentImages
            .Include(fi => fi.File)
            .Include(fi => fi.ThumbnailFile)
            .FirstOrDefaultAsync(fi => fi.FilamentId == filamentId
                                    && fi.Id == imageId
                                    && fi.Filament.CreatedById == userId, ct)
            ?? throw new DoesNotExistException();

        var blobNames = new[] { image.File?.Path, image.ThumbnailFile?.Path }
            .Where(p => p is not null)
            .Select(p => Path.GetFileName(p)!)
            .ToList();

        var wasDefault = image.IsDefault;

        context.FilamentImages.Remove(image);
        if (image.File is not null) context.Files.Remove(image.File);
        if (image.ThumbnailFile is not null) context.Files.Remove(image.ThumbnailFile);

        await context.SaveChangesAsync(ct);

        if (wasDefault)
        {
            // Separate save, after the old default row is gone, so the filtered unique
            // index never sees two defaults at once.
            var next = await context.FilamentImages
                .Where(fi => fi.FilamentId == filamentId)
                .OrderBy(fi => fi.DisplayOrder).ThenBy(fi => fi.Id)
                .FirstOrDefaultAsync(ct);
            if (next is not null)
            {
                next.IsDefault = true;
                await context.SaveChangesAsync(ct);
            }
        }

        // Blobs LAST. ProjectService deletes them first, so a failed SaveChangesAsync
        // there leaves a row pointing at destroyed bytes. This ordering fails toward an
        // orphaned blob (wasted storage) instead of a broken record.
        foreach (var name in blobNames)
            await blobStorage.DeleteBlobAsync(BlobContainers.FilamentImages, name);
    }

    public async Task ReorderImagesAsync(
        Guid filamentId, IList<int> orderedImageIds, long userId, CancellationToken ct = default)
    {
        var images = await context.FilamentImages
            .Where(fi => fi.FilamentId == filamentId && fi.Filament.CreatedById == userId)
            .ToListAsync(ct);

        // Exact-set validation, matching the print endpoint. ProjectService silently
        // ignores unknown IDs and accepts partial lists, which hides client bugs.
        var supplied = orderedImageIds?.ToList() ?? [];
        if (supplied.Count == 0
            || supplied.Count != supplied.Distinct().Count()
            || supplied.Count != images.Count
            || !supplied.OrderBy(i => i).SequenceEqual(images.Select(i => i.Id).OrderBy(i => i)))
        {
            throw new ArgumentException("Image IDs do not match filament images");
        }

        for (var i = 0; i < supplied.Count; i++)
            images.First(im => im.Id == supplied[i]).DisplayOrder = i;

        await context.SaveChangesAsync(ct);
    }

    public async Task SetDefaultImageAsync(
        Guid filamentId, int imageId, long userId, CancellationToken ct = default)
    {
        var owns = await context.FilamentImages.AnyAsync(
            fi => fi.FilamentId == filamentId && fi.Id == imageId
               && fi.Filament.CreatedById == userId, ct);
        if (!owns) throw new DoesNotExistException();

        // One statement, so the filtered unique index never observes two defaults.
        // Clearing and setting via separate tracked updates in a single SaveChangesAsync
        // is NOT safe here: EF does not guarantee the clear is emitted first.
        await context.FilamentImages
            .Where(fi => fi.FilamentId == filamentId)
            .ExecuteUpdateAsync(s => s.SetProperty(fi => fi.IsDefault, fi => fi.Id == imageId), ct);

        // ExecuteUpdateAsync goes straight to the database and does not notify the change
        // tracker, so anything already loaded in this scope still carries the old flag and
        // a re-read in the same request would serve it. Reconcile the tracked copies.
        // Materialized before the loop: writing to a tracked entity mutates the change
        // tracker, and enumerating Entries() lazily while doing so throws.
        var tracked = context.ChangeTracker.Entries<FilamentImage>()
            .Where(e => e.Entity.FilamentId == filamentId)
            .ToList();

        foreach (var entry in tracked)
        {
            var isDefault = entry.Entity.Id == imageId;
            var property = entry.Property(fi => fi.IsDefault);

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
        byte[] bytes, string extension, long userId, CancellationToken ct)
    {
        var blobName = $"{Guid.NewGuid()}{extension}";
        using var stream = new MemoryStream(bytes);
        await blobStorage.UploadAsync(BlobContainers.FilamentImages, blobName, stream);

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
    /// Filament images join the same account-wide byte accounting as print attachments:
    /// a per-filament count cap bounds nothing on its own, because nothing caps how many
    /// filaments a user may create.
    /// </summary>
    private async Task EnsureAccountStorageQuotaAsync(long userId, long newBytes, CancellationToken ct)
    {
        var attachmentBytes = await context.PrintAttachments
            .Where(pa => pa.CreatedById == userId)
            .SumAsync(pa => (long?)pa.File.Size, ct) ?? 0L;

        var imageBytes = await context.FilamentImages
            .Where(fi => fi.CreatedById == userId)
            .SumAsync(fi => (long?)fi.File.Size + (fi.ThumbnailFile != null ? fi.ThumbnailFile.Size : 0L), ct) ?? 0L;

        if (attachmentBytes + imageBytes + newBytes > SubscriptionLimits.ProMaxFileStorageBytes)
            throw new BadRequestException("Storage quota exceeded. Delete files to free up space.");
    }
}
