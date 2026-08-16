using System;
using System.IO;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;

namespace PrintLogApi.Services
{
    /// <summary>
    /// Azure Blob Storage implementation for file uploads.
    /// </summary>
    public class AzureBlobStorageService : IBlobStorageService
    {
        private readonly BlobServiceClient _blobServiceClient;

        public AzureBlobStorageService(IConfiguration configuration)
        {
            var connectionString = configuration["AZURE_STORAGE_CONNECTION_STRING"];
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("Azure Storage connection string is required for blob operations.");
            _blobServiceClient = new BlobServiceClient(connectionString);
        }

        /// <summary>
        /// Uploads a file stream to Azure Blob Storage.
        /// </summary>
        public async Task<BlobUploadResult> UploadAsync(string containerName, string blobName, Stream stream)
        {
            var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
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
}
