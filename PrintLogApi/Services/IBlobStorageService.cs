using System;
using System.IO;
using System.Threading.Tasks;

namespace PrintLogApi.Services
{
    /// <summary>
    /// Result of a blob upload operation.
    /// </summary>
    public class BlobUploadResult
    {
        /// <summary>
        /// The blob path relative to storage (e.g., "container/filename.jpg")
        /// </summary>
        public string BlobPath { get; set; }

        /// <summary>
        /// The public URI/URL of the blob for accessing it.
        /// </summary>
        public Uri BlobUri { get; set; }
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
    }
}
