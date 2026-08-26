using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;

namespace PrintLogApi.Services;

/// <summary>
/// Azure Blob Storage implementation for file uploads.
/// </summary>
public class AzureBlobStorageService : IBlobStorageService
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly TimeProvider _timeProvider;
    private readonly IBlobContainerProvisioner _containerProvisioner;

    public AzureBlobStorageService(
        IConfiguration configuration,
        TimeProvider timeProvider,
        IBlobContainerProvisioner containerProvisioner)
    {
        var connectionString = configuration["AZURE_STORAGE_CONNECTION_STRING"];
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Azure Storage connection string is required for blob operations.");
        _blobServiceClient = new BlobServiceClient(connectionString);
        _timeProvider = timeProvider;
        _containerProvisioner = containerProvisioner;
    }

    /// <summary>
    /// Uploads a file stream to Azure Blob Storage.
    /// </summary>
    public async Task<BlobUploadResult> UploadAsync(string containerName, string blobName, Stream stream)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
        await _containerProvisioner.EnsureAsync(
            containerName,
            () => containerClient.CreateIfNotExistsAsync(PublicAccessType.None));

        var blobClient = containerClient.GetBlobClient(blobName);

        await blobClient.UploadAsync(stream, overwrite: true);

        return new BlobUploadResult
        {
            BlobPath = $"{containerName}/{blobName}",
            BlobUri = blobClient.Uri
        };
    }

    /// <summary>
    /// Generates a time-limited SAS URL that allows a client to PUT a file directly to Azure Blob Storage.
    /// </summary>
    public Task<Uri> GenerateSasUploadUrlAsync(string containerName, string blobName, TimeSpan expiry)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
        var blobClient = containerClient.GetBlobClient(blobName);

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = containerName,
            BlobName = blobName,
            Resource = "b",
            ExpiresOn = DateTimeOffset.UtcNow.Add(expiry),
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Write | BlobSasPermissions.Create);

        return Task.FromResult(blobClient.GenerateSasUri(sasBuilder));
    }

    /// <summary>
    /// Generates a time-limited SAS URL that allows a client to GET/download a blob.
    /// Includes Content-Disposition and Content-Type headers in the SAS so the browser handles the download correctly.
    /// </summary>
    public Task<Uri> GenerateSasDownloadUrlAsync(
        string containerName,
        string blobName,
        string contentType,
        string originalFileName,
        TimeSpan expiry)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
        var blobClient = containerClient.GetBlobClient(blobName);

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = containerName,
            BlobName = blobName,
            Resource = "b",
            ExpiresOn = DateTimeOffset.UtcNow.Add(expiry),
            ContentType = contentType,
            ContentDisposition = $"attachment; filename=\"{originalFileName.Replace("\\", "_").Replace("\"", "_")}\"",
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        return Task.FromResult(blobClient.GenerateSasUri(sasBuilder));
    }

    /// <summary>
    /// Generates a time-limited SAS URL for displaying a blob inline in the browser.
    /// Expiry is bucketed so repeated calls within a window return a byte-identical URL,
    /// which is what lets the browser's image cache hit at all.
    /// </summary>
    public Task<Uri> GenerateSasInlineUrlAsync(
        string containerName, string blobName, string contentType,
        TimeSpan bucketSize, TimeSpan cacheControlMaxAge)
    {
        if (cacheControlMaxAge >= bucketSize)
            throw new ArgumentException(
                "Cache max-age must be shorter than the bucket size, otherwise a cached " +
                "response can outlive the SAS signature that fetched it.",
                nameof(cacheControlMaxAge));

        var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
        var blobClient = containerClient.GetBlobClient(blobName);

        if (!blobClient.CanGenerateSasUri)
            throw new InvalidOperationException(
                "Blob client cannot generate a SAS URI. Filament images require a " +
                "shared-key connection string.");

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = containerName,
            BlobName = blobName,
            Resource = "b",
            ExpiresOn = CeilingToBucket(_timeProvider.GetUtcNow(), bucketSize) + bucketSize,
            ContentType = contentType,
            ContentDisposition = "inline",
            CacheControl = $"private, max-age={(int)cacheControlMaxAge.TotalSeconds}",
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        return Task.FromResult(blobClient.GenerateSasUri(sasBuilder));
    }

    /// <summary>
    /// Rounds an instant up to the next multiple of <paramref name="bucketSize"/>, so every
    /// caller inside one bucket signs the same expiry and therefore the same URL.
    /// </summary>
    private static DateTimeOffset CeilingToBucket(DateTimeOffset instant, TimeSpan bucketSize)
    {
        var ticks = bucketSize.Ticks;
        var rounded = (instant.UtcTicks + ticks - 1) / ticks * ticks;
        return new DateTimeOffset(rounded, TimeSpan.Zero);
    }

    /// <summary>
    /// Downloads a blob from Azure Blob Storage. Returns null if the blob does not exist.
    /// </summary>
    public async Task<(Stream stream, string fileName)?> DownloadAsync(string containerName, string blobName)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
        var blobClient = containerClient.GetBlobClient(blobName);

        if (!await blobClient.ExistsAsync())
            return null;

        var ms = new MemoryStream();
        await blobClient.DownloadToAsync(ms);
        ms.Position = 0;
        return (ms, Path.GetFileName(blobName));
    }

    /// <summary>
    /// Deletes a blob from Azure Blob Storage. Does nothing if the blob does not exist.
    /// </summary>
    public async Task DeleteBlobAsync(string containerName, string blobName)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
        var blobClient = containerClient.GetBlobClient(blobName);
        await blobClient.DeleteIfExistsAsync();
    }
}
