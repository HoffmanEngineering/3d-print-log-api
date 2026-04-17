using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PrintLogApi.Services;

namespace PrintLogApi.IntegrationTests
{
    /// <summary>
    /// In-memory blob storage implementation for integration testing.
    /// Stores files in memory without actual blob storage calls.
    /// </summary>
    public class InMemoryBlobStorageService : IBlobStorageService
    {
        /// <summary>
        /// Dictionary to store uploaded blobs in memory for testing.
        /// Key: container/blobname, Value: file content as bytes
        /// </summary>
        public Dictionary<string, byte[]> Blobs { get; private set; } = new();

        /// <summary>
        /// Base URI for test blobs (can be customized for testing).
        /// </summary>
        public Uri BaseUri { get; set; } = new Uri("https://test.blob.core.windows.net/");

        /// <summary>
        /// Uploads a file stream to in-memory storage.
        /// </summary>
        public async Task<BlobUploadResult> UploadAsync(string containerName, string blobName, Stream stream)
        {
            var blobPath = $"{containerName}/{blobName}";

            // Read stream to byte array
            using (var memoryStream = new MemoryStream())
            {
                await stream.CopyToAsync(memoryStream);
                Blobs[blobPath] = memoryStream.ToArray();
            }

            // Construct the blob URI from the base URI and blob path
            var blobUri = new Uri(BaseUri, blobPath);

            return new BlobUploadResult
            {
                BlobPath = blobPath,
                BlobUri = blobUri
            };
        }

        /// <summary>
        /// Returns a dummy SAS upload URI for testing purposes.
        /// </summary>
        public Task<Uri> GenerateSasUploadUrlAsync(string containerName, string blobName, TimeSpan expiry)
            => Task.FromResult(new Uri("https://fake-blob-storage.example.com/upload-sas"));

        /// <summary>
        /// Returns a dummy SAS download URI for testing purposes.
        /// </summary>
        public Task<Uri> GenerateSasDownloadUrlAsync(string containerName, string blobName, string contentType, string originalFileName, TimeSpan expiry)
            => Task.FromResult(new Uri("https://fake-blob-storage.example.com/download-sas"));

        /// <summary>
        /// Downloads a blob from in-memory storage. Returns null if it does not exist.
        /// </summary>
        public Task<(Stream stream, string fileName)?> DownloadAsync(string containerName, string blobName)
        {
            var blobPath = $"{containerName}/{blobName}";
            if (!Blobs.TryGetValue(blobPath, out var bytes))
                return Task.FromResult<(Stream, string)?>(null);

            Stream ms = new MemoryStream(bytes);
            return Task.FromResult<(Stream, string)?>((ms, blobName));
        }

        /// <summary>
        /// Removes the blob from in-memory storage. No-op if it does not exist.
        /// </summary>
        public Task DeleteBlobAsync(string containerName, string blobName)
        {
            Blobs.Remove($"{containerName}/{blobName}");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Clears all stored blobs (useful between tests).
        /// </summary>
        public void Clear()
        {
            Blobs.Clear();
        }

        /// <summary>
        /// Checks if a blob exists in storage.
        /// </summary>
        public bool BlobExists(string blobPath)
        {
            return Blobs.ContainsKey(blobPath);
        }
    }
}
