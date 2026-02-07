using System.IO;
using System.Threading.Tasks;

namespace PrintLogApi.Services
{
    /// <summary>
    /// Service for managing blob storage operations (file uploads).
    /// </summary>
    public interface IBlobStorageService
    {
        /// <summary>
        /// Uploads a file stream to blob storage and returns the blob path.
        /// </summary>
        /// <param name="containerName">The blob container name</param>
        /// <param name="blobName">The name/path of the blob file</param>
        /// <param name="stream">The file stream to upload</param>
        /// <returns>The full blob path (container/blobName)</returns>
        Task<string> UploadAsync(string containerName, string blobName, Stream stream);
    }
}
