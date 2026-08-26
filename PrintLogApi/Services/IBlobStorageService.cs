namespace PrintLogApi.Services;

/// <summary>
/// Result of a blob upload operation.
/// </summary>
public class BlobUploadResult
{
    /// <summary>
    /// The blob path relative to storage (e.g., "container/filename.jpg")
    /// </summary>
    /// <remarks>
    /// Null-forgiven rather than nullable: this type is never deserialized, and its single
    /// construction site (AzureBlobStorageService.UploadAsync) always assigns both properties.
    /// </remarks>
    public string BlobPath { get; set; } = null!;

    /// <summary>
    /// The public URI/URL of the blob for accessing it.
    /// </summary>
    public Uri BlobUri { get; set; } = null!;
}

/// <summary>
/// Service for managing blob storage operations (file uploads).
/// </summary>
public interface IBlobStorageService
{
    /// <summary>
    /// Uploads a file stream to blob storage.
    /// </summary>
    /// <param name="containerName">The blob container name</param>
    /// <param name="blobName">The name/path of the blob file</param>
    /// <param name="stream">The file stream to upload</param>
    /// <returns>Upload result containing blob path and URI</returns>
    Task<BlobUploadResult> UploadAsync(string containerName, string blobName, Stream stream);

    /// <summary>
    /// Generates a time-limited SAS URL that allows a client to PUT a file directly to Azure Blob Storage.
    /// </summary>
    Task<Uri> GenerateSasUploadUrlAsync(string containerName, string blobName, TimeSpan expiry);

    /// <summary>
    /// Generates a time-limited SAS URL that allows a client to GET/download a blob.
    /// Includes Content-Disposition and Content-Type headers in the SAS so the browser handles the download correctly.
    /// </summary>
    Task<Uri> GenerateSasDownloadUrlAsync(
        string containerName,
        string blobName,
        string contentType,
        string originalFileName,
        TimeSpan expiry);

    /// <summary>
    /// Generates a time-limited SAS URL for displaying a blob inline in the browser.
    /// </summary>
    /// <param name="bucketSize">
    /// Expiry is rounded up to a multiple of this, so repeated calls within a window
    /// return a byte-identical URL. Browsers key their image cache on the URL, so an
    /// unstable URL means the cache never hits and SAS buys nothing over proxying.
    /// </param>
    /// <param name="cacheControlMaxAge">
    /// Emitted as Cache-Control. Must be strictly less than <paramref name="bucketSize"/>
    /// so a cached response cannot outlive the signature that fetched it.
    /// </param>
    Task<Uri> GenerateSasInlineUrlAsync(
        string containerName,
        string blobName,
        string contentType,
        TimeSpan bucketSize,
        TimeSpan cacheControlMaxAge);

    /// <summary>
    /// Downloads a blob and returns its content as a stream.
    /// Returns null if the blob does not exist.
    /// </summary>
    Task<(Stream stream, string fileName)?> DownloadAsync(string containerName, string blobName);

    /// <summary>
    /// Deletes a blob from storage. Does nothing if the blob does not exist.
    /// </summary>
    /// <param name="containerName">The blob container name</param>
    /// <param name="blobName">The name/path of the blob file</param>
    Task DeleteBlobAsync(string containerName, string blobName);
}
