using Microsoft.EntityFrameworkCore;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;
using static PrintLogApi.Services.ImageBackfill.ImageBackfillRunner;

namespace PrintLogApi.Services.ImageBackfill;

/// <summary>
/// Repairs rows the image backfill converted before it kept transparency: their original was
/// re-encoded as a JPEG, which turned a transparent background black. For each such row whose
/// untouched original (in the archive container) has transparent pixels, writes a PNG original
/// and a social image on white. Rows whose archived original is opaque are right already and are
/// not touched. Run by <c>PrintLogApi.Tools image-backfill --repair-transparency</c>; the runbook is
/// docs/image-backfill.md.
///
/// <para><b>Finding a row's archive copy.</b> The database does not record it: the backfill
/// pointed the existing File row at the new blob and kept no old name. Each archive copy is tied
/// to a row by any of three links, and must be tied to exactly one row (and each row to one copy)
/// or it is reported and left alone. Nothing is matched by time or by guess.</para>
/// <list type="number">
/// <item>Its blob metadata (<see cref="ArchiveMetadata"/>), which the backfill writes from now on.</item>
/// <item>A line of the run's log (<see cref="BackfillLog"/>).</item>
/// <item>Its name: legacy print uploads were stored as <c>{File id}{client extension}</c>, and the
/// backfill keeps the File row's id, so a copy named after a File id belongs to the image row with
/// that original. This is what finds the rows converted after a run's log stopped.</item>
/// </list>
/// <para>A matched row is repaired only if it is still the backfill's JPEG and the archived image
/// decodes to the row's width and height, which a wrong match would not.</para>
///
/// <para>Per row, in this order: process the archive copy; upload the new PNG original and the
/// social image under new names; point the row at them and remove the old social File row in one
/// save; only then delete the old JPEG original and social blobs. The archive copy is never
/// touched. A failure before the save commits deletes what that row wrote and leaves it as it was.</para>
/// </summary>
public sealed class ImageTransparencyRepairRunner(
    PrintLogContext context,
    IBlobStorageService blobStorage,
    IImageProcessingService imageProcessing,
    IProcessedImageWriter imageWriter,
    TextWriter log)
{
    private const int FileIdLookupChunk = 500;

    public async Task<TransparencyRepairReport> RunAsync(TransparencyRepairOptions options, CancellationToken ct = default)
    {
        if (options.Limit is < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Limit must be at least 1.");

        var report = new TransparencyRepairReport { Applied = options.Apply, LogEntries = options.FromLog.Count };
        var remaining = options.Limit ?? int.MaxValue;

        remaining = await RunKindAsync(PrintImages, context.PrintImages, options, remaining, report, ct);
        await RunKindAsync(ProjectImages, context.ProjectImages, options, remaining, report, ct);
        return report;
    }

    private async Task<int> RunKindAsync<T>(
        ImageKind kind, IQueryable<T> scope, TransparencyRepairOptions options, int remaining,
        TransparencyRepairReport report, CancellationToken ct)
        where T : class, IStoredImage
    {
        var matches = await MatchAsync(kind, scope, options.FromLog, report, ct);
        foreach (var (id, blobName) in matches.OrderBy(m => m.Key))
        {
            if (remaining <= 0) break;
            ct.ThrowIfCancellationRequested();
            if (await RepairRowAsync(kind, scope, id, blobName, options.Apply, report, ct))
                remaining--;
        }
        return remaining;
    }

    /// <summary>Ties each archive copy of this kind to one row: row id to archive blob name.</summary>
    private async Task<Dictionary<int, string>> MatchAsync<T>(
        ImageKind kind, IQueryable<T> scope, IReadOnlyList<ArchivedOriginal> fromLog,
        TransparencyRepairReport report, CancellationToken ct)
        where T : class, IStoredImage
    {
        var listed = new Dictionary<string, StoredBlob>(StringComparer.Ordinal);
        await foreach (var blob in blobStorage.ListBlobsAsync(kind.ArchiveContainer, ct))
            listed[blob.Name] = blob;
        report.ArchiveBlobs += listed.Count;

        // Every row that claims each archive copy, and by which link.
        var claims = new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
        void Claim(string blobName, int rowId, string how)
        {
            if (!claims.TryGetValue(blobName, out var rows))
                claims[blobName] = rows = [];
            rows.TryAdd(rowId, how);
        }

        // 1. The log. A row logged under two names (it was converted, restored by hand, and
        //    converted again) is ambiguous: each of its copies becomes a conflict below.
        foreach (var entry in fromLog.Where(e => e.Image.Kind == kind.Name).DistinctBy(e => (e.Image.Id, e.BlobName)))
        {
            if (!listed.ContainsKey(entry.BlobName))
            {
                report.Skipped.Add(new ImageIssue(entry.Image,
                    $"the log names {kind.ArchiveContainer}/{entry.BlobName}, which is not in the archive"));
                log.WriteLine($"{entry.Image}: skipped, its logged archive copy {kind.ArchiveContainer}/{entry.BlobName} does not exist");
                continue;
            }
            Claim(entry.BlobName, entry.Image.Id, "the log");
        }

        // 2. Metadata written by the backfill.
        foreach (var blob in listed.Values)
        {
            if (ArchiveMetadata.ImageIdOf(blob.Metadata, kind.Name) is { } id)
                Claim(blob.Name, id, "its metadata");
        }

        // 3. A name that is a File id. Looked up in chunks; a GUID matches at most one File.
        var byFileId = listed.Keys
            .Select(name => (name, ok: Guid.TryParse(Path.GetFileNameWithoutExtension(name), out var g), id: g))
            .Where(x => x.ok)
            .GroupBy(x => x.id, x => x.name)
            .ToDictionary(g => g.Key, g => g.ToList());
        foreach (var chunk in byFileId.Keys.Chunk(FileIdLookupChunk))
        {
            var rows = await scope.AsNoTracking()
                .Where(i => chunk.Contains(i.FileId))
                .Select(i => new { i.Id, i.FileId })
                .ToListAsync(ct);
            foreach (var row in rows)
                foreach (var name in byFileId[row.FileId])
                    Claim(name, row.Id, "its name (the row's File id)");
        }

        // Resolve: one row per copy, one copy per row, or it is left for a human.
        var rowToBlobs = new Dictionary<int, List<string>>();
        foreach (var name in listed.Keys.Order(StringComparer.Ordinal))
        {
            var path = $"{kind.ArchiveContainer}/{name}";
            if (!claims.TryGetValue(name, out var rows))
            {
                var modified = listed[name].LastModified is { } at ? $", archived {at:u}" : "";
                report.Unmatched.Add(new ArchiveIssue(path,
                    $"no log line, metadata or {kind.Name} File id links it to a row{modified}"));
                log.WriteLine($"{path}: unmatched, no row could be tied to it");
                continue;
            }
            if (rows.Count > 1)
            {
                var who = string.Join("; ", rows.Select(r => $"{kind.Name} {r.Key} by {r.Value}"));
                report.Conflicts.Add(new ArchiveIssue(path, $"claimed by more than one row: {who}"));
                log.WriteLine($"{path}: conflict, claimed by more than one row");
                continue;
            }
            var rowId = rows.Keys.Single();
            if (!rowToBlobs.TryGetValue(rowId, out var blobs))
                rowToBlobs[rowId] = blobs = [];
            blobs.Add(name);
        }

        var matches = new Dictionary<int, string>();
        foreach (var (rowId, blobs) in rowToBlobs)
        {
            if (blobs.Count > 1)
            {
                foreach (var name in blobs)
                    report.Conflicts.Add(new ArchiveIssue($"{kind.ArchiveContainer}/{name}",
                        $"{kind.Name} {rowId} is tied to more than one archive copy: {string.Join(", ", blobs)}"));
                log.WriteLine($"{kind.Name} {rowId}: conflict, tied to more than one archive copy");
                continue;
            }
            matches[rowId] = blobs[0];
        }
        report.Matched += matches.Count;
        return matches;
    }

    /// <summary>
    /// Returns true when the row counts against the limit: repaired, would be, or a write failed.
    /// </summary>
    private async Task<bool> RepairRowAsync<T>(
        ImageKind kind, IQueryable<T> scope, int id, string archiveName, bool apply,
        TransparencyRepairReport report, CancellationToken ct)
        where T : class, IStoredImage
    {
        context.ChangeTracker.Clear();
        var row = new ImageRef(kind.Name, id);
        var archivePath = $"{kind.ArchiveContainer}/{archiveName}";
        try
        {
            var image = await scope
                .Include(nameof(IStoredImage.File))
                .Include(nameof(IStoredImage.SocialFile))
                .Where(i => i.Id == id)
                .FirstOrDefaultAsync(ct);
            if (image is null)
            {
                Skip(report, row, $"the row no longer exists (archive copy {archivePath})");
                return false;
            }
            if (image.ContentType is null)
            {
                report.NotConverted++;
                log.WriteLine($"{row}: not converted yet, left for the image backfill ({archivePath})");
                return false;
            }
            if (string.Equals(image.ContentType, "image/png", StringComparison.OrdinalIgnoreCase))
            {
                report.AlreadyRepaired++;
                return false;
            }
            if (!string.Equals(image.ContentType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
            {
                Skip(report, row, $"its content type is {image.ContentType}, not the backfill's image/jpeg");
                return false;
            }

            var download = await blobStorage.DownloadAsync(kind.ArchiveContainer, archiveName);
            if (download is null)
            {
                Skip(report, row, $"{archivePath} disappeared after it was listed");
                return false;
            }
            byte[] archived;
            await using (var stream = download.Value.stream)
            using (var buffer = new MemoryStream())
            {
                await stream.CopyToAsync(buffer, ct);
                archived = buffer.ToArray();
            }

            ProcessedImage processed;
            try
            {
                processed = await imageProcessing.ProcessAsync(
                    new MemoryStream(archived), ImageProcessingOptions.LegacyBackfill, ct);
            }
            catch (InvalidImageException ex)
            {
                Skip(report, row, $"{archivePath} cannot be decoded: {ex.Message}");
                return false;
            }

            // The backfill set Width and Height from these same bytes, so a copy of another size
            // is not this row's original.
            if (processed.Width != image.Width || processed.Height != image.Height)
            {
                Skip(report, row,
                    $"{archivePath} is {processed.Width}x{processed.Height} but the row is " +
                    $"{image.Width}x{image.Height}; not treated as the same image");
                return false;
            }

            if (processed.ContentType != "image/png")
            {
                report.Opaque++;
                log.WriteLine($"{row}: opaque, left as it is ({archivePath})");
                return false;
            }

            if (processed.Social is null)
            {
                report.Failed.Add(new ImageIssue(row, "the social image could not be generated"));
                log.WriteLine($"{row}: failed, no social image");
                return true;
            }

            var bytesBefore = image.File.Size + (image.SocialFile?.Size ?? 0);
            var bytesAfter = processed.Original.LongLength + processed.Social.LongLength;

            if (apply && !await ApplyAsync(kind, image, processed, row, report, ct))
                return true;

            report.Repaired++;
            report.BytesBefore += bytesBefore;
            report.BytesAfter += bytesAfter;
            log.WriteLine(
                $"{row}: {(apply ? "repaired" : "would be repaired")} from {archivePath}: " +
                $"image/jpeg -> image/png + social on white, {bytesBefore} -> {bytesAfter} bytes");
            return true;
        }
        finally
        {
            context.ChangeTracker.Clear();
        }
    }

    private void Skip(TransparencyRepairReport report, ImageRef row, string reason)
    {
        report.Skipped.Add(new ImageIssue(row, reason));
        log.WriteLine($"{row}: skipped, {reason}");
    }

    /// <summary>Writes one row. Returns false, with the row left as it was, when a step failed.</summary>
    private async Task<bool> ApplyAsync(
        ImageKind kind, IStoredImage image, ProcessedImage processed, ImageRef row,
        TransparencyRepairReport report, CancellationToken ct)
    {
        var uploaded = new List<string>();
        var fileId = image.File.Id;
        var oldOriginal = Path.GetFileName(image.File.Path);
        var oldSocial = image.SocialFile;
        var oldSocialBlob = Path.GetFileName(oldSocial?.Path);
        string? newOriginalPath = null;
        Models.File? social = null;
        try
        {
            await imageWriter.ReplaceOriginalAsync(kind.Container, image.File, processed, uploaded, ct);
            newOriginalPath = image.File.Path;
            image.ContentType = processed.ContentType;

            social = await imageWriter.WriteSocialAsync(kind.Container, processed, image.CreatedById, uploaded, ct);
            ProcessedImageWriter.SetSocial(image, social, processed);
            if (oldSocial is not null)
                context.Files.Remove(oldSocial);

            // One save: the original's File row, the image row and both social File rows.
            await context.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            context.ChangeTracker.Clear();
            var outcome = await CommittedAsync(context, fileId, newOriginalPath, social?.Id);
            if (outcome == true)
            {
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
                    report.Failed.Add(new ImageIssue(row,
                        $"{ex.GetType().Name}: {ex.Message}; could not read back whether the save " +
                        $"committed, so the new blobs were kept: {string.Join(", ", uploaded)}"));
                }
                log.WriteLine($"{row}: failed, {ex.GetType().Name}");
                if (ex is OperationCanceledException) throw;
                return false;
            }
        }

        // Only now, with the row committed: the black-backed JPEGs nothing points at any more.
        foreach (var name in new[] { oldOriginal, oldSocialBlob })
        {
            if (string.IsNullOrEmpty(name)) continue;
            try
            {
                await blobStorage.DeleteBlobAsync(kind.Container, name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                report.OldBlobsNotDeleted.Add(new ImageIssue(row, $"{kind.Container}/{name}: {ex.Message}"));
            }
        }
        return true;
    }
}
