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

        var thumbnail = processed.Thumbnail is null
            ? null
            : await UploadAndRecordAsync(
                containerName, processed.Thumbnail, ImageContentTypes.ExtensionFor(ThumbnailContentType),
                userId, uploadedBlobNames, ct);

        return new StoredImageFiles(original, thumbnail);
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
        ct.ThrowIfCancellationRequested();

        var fileId = Guid.NewGuid();
        var blobName = $"{fileId}{extension}";
        using var stream = new MemoryStream(bytes);
        var upload = await blobStorage.UploadAsync(containerName, blobName, stream);
        uploadedBlobNames.Add(blobName);

        // Path keeps the "container/name" form print images have always stored, which
        // UserDeletionService and the download paths read.
        var file = new Models.File
        {
            Id = fileId,
            Path = upload.BlobPath,
            Size = bytes.LongLength,
            CreatedById = userId,
            UpdatedById = userId,
        };
        context.Files.Add(file);
        return file;
    }
}
