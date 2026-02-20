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
