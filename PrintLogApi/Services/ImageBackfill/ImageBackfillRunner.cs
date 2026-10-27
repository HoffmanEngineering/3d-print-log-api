using Microsoft.EntityFrameworkCore;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;

namespace PrintLogApi.Services.ImageBackfill;

/// <summary>
/// Re-processes legacy print and project images to the canonical format with thumbnails: the
/// shape <c>ProcessedImageWriter</c> gives a new upload. Run by <c>PrintLogApi.Tools
/// image-backfill</c>; the runbook is docs/image-backfill.md.
///
/// <para>Per row, in this order: download the original; process it (an undecodable image stops
/// here, untouched); copy the untouched bytes to the private archive container; upload the new
/// original and thumbnail under new names; point the row at them in one save; only then delete
/// the old original. A failure before the save commits deletes the blobs that row wrote and
/// leaves the row as it was, so the next run picks it up again.</para>
/// </summary>
public sealed class ImageBackfillRunner(
    PrintLogContext context,
    IBlobStorageService blobStorage,
    IImageProcessingService imageProcessing,
    IProcessedImageWriter imageWriter,
    TextWriter log)
{
    private sealed record ImageKind(string Name, string Container, string ArchiveContainer);

    private static readonly ImageKind PrintImages =
        new(ImageRef.PrintImage, BlobContainers.PrintImages, BlobContainers.PrintImagesArchive);

    private static readonly ImageKind ProjectImages =
        new(ImageRef.ProjectImage, BlobContainers.ProjectImages, BlobContainers.ProjectImagesArchive);

    private static readonly HashSet<string> LegacySourceTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/gif",
        "image/bmp",
    };

    public async Task<ImageBackfillReport> RunAsync(ImageBackfillOptions options, CancellationToken ct = default)
    {
        if (options.BatchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "BatchSize must be at least 1.");
        if (options.Limit is < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Limit must be at least 1.");

        var report = new ImageBackfillReport { Applied = options.Apply };
        var remaining = options.Limit ?? int.MaxValue;
        var unfiltered = options.PrintId is null && options.ProjectId is null;

        if (unfiltered || options.PrintId is not null)
        {
            IQueryable<PrintImage> prints = context.PrintImages;
            if (options.PrintId is { } printId)
                prints = prints.Where(i => i.PrintId == printId);
            remaining = await RunKindAsync(PrintImages, prints, options, remaining, report, ct);
        }

        if (unfiltered || options.ProjectId is not null)
        {
            IQueryable<ProjectImage> projects = context.ProjectImages;
            if (options.ProjectId is { } projectId)
                projects = projects.Where(i => i.ProjectId == projectId);
            await RunKindAsync(ProjectImages, projects, options, remaining, report, ct);
        }

        return report;
    }

    /// <summary>
    /// Walks the selected rows by ascending id. Keyset paging, not "the next N that still need
    /// work": a row that is skipped or fails stays selected, and must not be read again in the
    /// same run.
    /// </summary>
    private async Task<int> RunKindAsync<T>(
        ImageKind kind, IQueryable<T> scope, ImageBackfillOptions options, int remaining,
        ImageBackfillReport report, CancellationToken ct)
        where T : class, IStoredImage
    {
        var lastId = 0;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            var ids = await ImageBackfillSelection
                .NextBatch(scope, lastId, Math.Min(options.BatchSize, remaining))
                .ToListAsync(ct);
            if (ids.Count == 0) break;

            foreach (var id in ids)
            {
                if (await ProcessRowAsync(kind, scope, id, options.Apply, report, ct))
                    remaining--;
                lastId = id;
            }
        }
        return remaining;
    }

    /// <summary>Returns false when the row no longer needed work by the time it was read.</summary>
    private async Task<bool> ProcessRowAsync<T>(
        ImageKind kind, IQueryable<T> scope, int id, bool apply, ImageBackfillReport report, CancellationToken ct)
        where T : class, IStoredImage
    {
        // One row per unit of work: nothing a failed row left tracked can reach the next save.
        context.ChangeTracker.Clear();
        try
        {
            var image = await ImageBackfillSelection.Row(scope, id).FirstOrDefaultAsync(ct);
            var plan = image is null ? default : ImageBackfillSelection.Plan(image);
            if (image is null || plan.IsEmpty)
                return false;

            report.Examined++;
            var row = new ImageRef(kind.Name, id);

            var blobName = Path.GetFileName(image.File.Path);
            if (string.IsNullOrEmpty(blobName))
            {
                report.Failed.Add(new ImageIssue(row, "the file row has no blob path"));
                return true;
            }

            var download = await blobStorage.DownloadAsync(kind.Container, blobName);
            if (download is null)
            {
                report.Missing.Add(new ImageIssue(row, $"{kind.Container}/{blobName} does not exist"));
                log.WriteLine($"{row}: blob missing");
                return true;
            }

            byte[] original;
            await using (var stream = download.Value.stream)
            using (var buffer = new MemoryStream())
            {
                await stream.CopyToAsync(buffer, ct);
                original = buffer.ToArray();
            }

            ProcessedImage processed;
            try
            {
                processed = await imageProcessing.ProcessAsync(
                    new MemoryStream(original), ImageProcessingOptions.LegacyBackfill, ct);
            }
            catch (InvalidImageException ex)
            {
                report.Undecodable.Add(new ImageIssue(row, ex.Message));
                log.WriteLine($"{row}: skipped, {ex.Message}");
                return true;
            }

            if (!plan.ReplaceOriginal && processed.Thumbnail is null)
            {
                report.Failed.Add(new ImageIssue(row, "no thumbnail could be generated"));
                return true;
            }

            var thumbnailBytes = plan.AddThumbnail ? processed.Thumbnail?.LongLength ?? 0 : 0;
            var bytesAfter = (plan.ReplaceOriginal ? processed.Original.LongLength : original.LongLength) + thumbnailBytes;

            if (apply && !await ApplyAsync(kind, image, plan, blobName, original, processed, row, report, ct))
                return true;

            report.Processed++;
            if (!plan.ReplaceOriginal) report.ThumbnailOnly++;
            var converted = plan.ReplaceOriginal && processed.SourceContentType is { } source
                && LegacySourceTypes.Contains(source);
            if (converted) report.ConvertedFromLegacyFormat++;
            report.BytesBefore += original.LongLength;
            report.BytesAfter += bytesAfter;

            var what = plan.ReplaceOriginal
                ? $"{processed.SourceContentType ?? "unknown"} -> {processed.ContentType}{(thumbnailBytes > 0 ? " + thumbnail" : "")}"
                : "thumbnail only";
            // The old name is what an operator needs to put one image back from the archive.
            var archived = apply && plan.ReplaceOriginal ? $", original archived as {kind.ArchiveContainer}/{blobName}" : "";
            log.WriteLine($"{row}: {(apply ? "" : "would be ")}{what}, {original.LongLength} -> {bytesAfter} bytes{archived}");
            return true;
        }
        finally
        {
            context.ChangeTracker.Clear();
        }
    }

    /// <summary>Writes one row. Returns false, with the row left as it was, when a step failed.</summary>
    private async Task<bool> ApplyAsync(
        ImageKind kind, IStoredImage image, ImageBackfillPlan plan, string oldBlobName, byte[] original,
        ProcessedImage processed, ImageRef row, ImageBackfillReport report, CancellationToken ct)
    {
        var uploaded = new List<string>();
        var fileId = image.File.Id;
        string? newOriginalPath = null;
        Models.File? thumbnail = null;
        try
        {
            if (plan.ReplaceOriginal)
            {
                // First, before anything changes: the untouched bytes, in a private container.
                // Overwriting is fine; a retry after a failed attempt writes the same bytes.
                using (var archive = new MemoryStream(original))
                    await blobStorage.UploadAsync(kind.ArchiveContainer, oldBlobName, archive);

                await imageWriter.ReplaceOriginalAsync(kind.Container, image.File, processed, uploaded, ct);
                newOriginalPath = image.File.Path;
                image.ContentType = processed.ContentType;
                image.Width = processed.Width;
                image.Height = processed.Height;
            }

            if (plan.AddThumbnail)
            {
                thumbnail = await imageWriter.WriteThumbnailAsync(
                    kind.Container, processed, image.CreatedById, uploaded, ct);
                if (thumbnail is not null)
                    image.ThumbnailFile = thumbnail;
            }

            // One save, so the row's columns and both File rows change together.
            await context.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            context.ChangeTracker.Clear();
            var outcome = await CommittedAsync(fileId, newOriginalPath, thumbnail?.Id);
            if (outcome == true)
            {
                // The save reached the database and only its acknowledgement was lost.
                log.WriteLine($"{row}: the save reported {ex.GetType().Name} but committed");
            }
            else
            {
                if (outcome == false)
                {
                    await imageWriter.DeleteBlobsQuietlyAsync(kind.Container, uploaded);
                    report.Failed.Add(new ImageIssue(row, $"{ex.GetType().Name}: {ex.Message}"));
                }
                else
                {
                    // Unknown: deleting the new blobs could break a row that did commit.
                    report.Failed.Add(new ImageIssue(row,
                        $"{ex.GetType().Name}: {ex.Message}; could not read back whether the save " +
                        $"committed, so the new blobs were kept: {string.Join(", ", uploaded)}"));
                }
                log.WriteLine($"{row}: failed, {ex.GetType().Name}");
                if (ex is OperationCanceledException) throw;
                return false;
            }
        }

        if (plan.ReplaceOriginal)
        {
            try
            {
                await blobStorage.DeleteBlobAsync(kind.Container, oldBlobName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                report.OldBlobsNotDeleted.Add(new ImageIssue(row, $"{kind.Container}/{oldBlobName}: {ex.Message}"));
            }
        }
        return true;
    }

    /// <summary>
    /// Whether a save that threw had committed anyway: true, false, or null when that cannot be
    /// read. Either new File value is proof, since both were written in the same save.
    /// </summary>
    private async Task<bool?> CommittedAsync(Guid originalFileId, string? newOriginalPath, Guid? thumbnailFileId)
    {
        if (newOriginalPath is null && thumbnailFileId is null)
            return false; // Nothing was staged, so nothing could have committed.
        try
        {
            if (newOriginalPath is not null)
            {
                var path = await context.Files.AsNoTracking()
                    .Where(f => f.Id == originalFileId)
                    .Select(f => f.Path)
                    .FirstOrDefaultAsync(CancellationToken.None);
                return path == newOriginalPath;
            }
            return await context.Files.AsNoTracking().AnyAsync(f => f.Id == thumbnailFileId, CancellationToken.None);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
