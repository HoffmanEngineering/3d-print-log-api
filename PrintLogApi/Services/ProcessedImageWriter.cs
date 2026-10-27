namespace PrintLogApi.Services;

/// <summary>The File rows written for one processed upload. Added to the context, not saved.</summary>
public sealed record StoredImageFiles(Models.File Original, Models.File? Thumbnail);

/// <summary>
/// Stores a <see cref="ProcessedImage"/> for print and project images: the re-encoded original
/// and its thumbnail as blobs named with the canonical extension for their decoded type, plus a
/// File row for each. Printer and material images keep their own copy of this in their services.
/// </summary>
public interface IProcessedImageWriter
{
    /// <summary>
    /// Uploads both blobs and adds (does not save) their File rows. Every blob name written is
    /// appended to <paramref name="uploadedBlobNames"/> as soon as its upload succeeds, so a
    /// caller whose save then fails can remove them with <see cref="DeleteBlobsQuietlyAsync"/>.
    /// </summary>
    Task<StoredImageFiles> WriteAsync(
        string containerName, ProcessedImage processed, long userId,
        List<string> uploadedBlobNames, CancellationToken ct = default);

    /// <summary>
    /// Uploads only the thumbnail and adds (does not save) its File row. Null when the processor
    /// produced no thumbnail. Appends the blob name as <see cref="WriteAsync"/> does.
    /// </summary>
    Task<Models.File?> WriteThumbnailAsync(
        string containerName, ProcessedImage processed, long userId,
        List<string> uploadedBlobNames, CancellationToken ct = default);

    /// <summary>
    /// Uploads the re-encoded original under a new canonical name and points the existing
    /// (tracked) <paramref name="original"/> row at it: Path and Size change, the id does not.
    /// Nothing is saved, and the blob the row pointed at before is left in place; the caller
    /// deletes it once the save has committed. Appends the blob name as <see cref="WriteAsync"/> does.
    /// </summary>
    Task ReplaceOriginalAsync(
        string containerName, Models.File original, ProcessedImage processed,
        List<string> uploadedBlobNames, CancellationToken ct = default);

    /// <summary>
    /// Best-effort removal of blobs written for an upload that then failed. Swallows its own
    /// failures and takes no token: it runs on the way out of a failed (often canceled)
    /// request, and a throw here would replace the real exception.
    /// </summary>
    Task DeleteBlobsQuietlyAsync(string containerName, IEnumerable<string> blobNames);
}

/// <inheritdoc cref="IProcessedImageWriter"/>
public class ProcessedImageWriter(PrintLogContext context, IBlobStorageService blobStorage) : IProcessedImageWriter
{
    public const string ThumbnailContentType = "image/webp";

    public async Task<StoredImageFiles> WriteAsync(
        string containerName, ProcessedImage processed, long userId,
        List<string> uploadedBlobNames, CancellationToken ct = default)
    {
        var original = await UploadAndRecordAsync(
            containerName, processed.Original, ImageContentTypes.ExtensionFor(processed.ContentType),
            userId, uploadedBlobNames, ct);

        var thumbnail = await WriteThumbnailAsync(containerName, processed, userId, uploadedBlobNames, ct);

        return new StoredImageFiles(original, thumbnail);
    }

    public async Task<Models.File?> WriteThumbnailAsync(
        string containerName, ProcessedImage processed, long userId,
        List<string> uploadedBlobNames, CancellationToken ct = default)
        => processed.Thumbnail is null
            ? null
            : await UploadAndRecordAsync(
                containerName, processed.Thumbnail, ImageContentTypes.ExtensionFor(ThumbnailContentType),
                userId, uploadedBlobNames, ct);

    public async Task ReplaceOriginalAsync(
        string containerName, Models.File original, ProcessedImage processed,
        List<string> uploadedBlobNames, CancellationToken ct = default)
    {
        // A fresh name, never one derived from the row's id: a legacy print blob is often
        // already named {fileId}.jpg, and overwriting it before the save commits would lose the
        // bytes the row still points at.
        var (_, blobPath) = await UploadAsync(
            containerName, processed.Original, ImageContentTypes.ExtensionFor(processed.ContentType),
            uploadedBlobNames, ct);
        original.Path = blobPath;
        original.Size = processed.Original.LongLength;
    }

    public async Task DeleteBlobsQuietlyAsync(string containerName, IEnumerable<string> blobNames)
    {
        foreach (var name in blobNames)
        {
            try
            {
                await blobStorage.DeleteBlobAsync(containerName, name);
            }
            catch
            {
                // Intentionally ignored - see the summary on the interface.
            }
        }
    }

    private async Task<Models.File> UploadAndRecordAsync(
        string containerName, byte[] bytes, string extension, long userId,
        List<string> uploadedBlobNames, CancellationToken ct)
    {
        var (fileId, blobPath) = await UploadAsync(containerName, bytes, extension, uploadedBlobNames, ct);

        var file = new Models.File
        {
            Id = fileId,
            Path = blobPath,
            Size = bytes.LongLength,
            CreatedById = userId,
            UpdatedById = userId,
        };
        context.Files.Add(file);
        return file;
    }

    /// <summary>Uploads <paramref name="bytes"/> as <c>{new id}{extension}</c>.</summary>
    private async Task<(Guid Id, string BlobPath)> UploadAsync(
        string containerName, byte[] bytes, string extension,
        List<string> uploadedBlobNames, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var id = Guid.NewGuid();
        var blobName = $"{id}{extension}";
        using var stream = new MemoryStream(bytes);
        var upload = await blobStorage.UploadAsync(containerName, blobName, stream);
        uploadedBlobNames.Add(blobName);

        // Path keeps the "container/name" form print images have always stored, which
        // UserDeletionService and the download paths read.
        return (id, upload.BlobPath);
    }
}
