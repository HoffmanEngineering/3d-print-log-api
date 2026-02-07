using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace PrintLogApi.Services
{
    /// <summary>
    /// In-memory blob storage implementation for testing.
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
        /// Uploads a file stream to in-memory storage.
        /// </summary>
        public async Task<string> UploadAsync(string containerName, string blobName, Stream stream)
        {
            var blobPath = $"{containerName}/{blobName}";

            // Read stream to byte array
            using (var memoryStream = new MemoryStream())
            {
                await stream.CopyToAsync(memoryStream);
                Blobs[blobPath] = memoryStream.ToArray();
            }

            return blobPath;
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
