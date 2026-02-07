using System.IO;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
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
            _blobServiceClient = new BlobServiceClient(connectionString);
        }

        /// <summary>
        /// Uploads a file stream to Azure Blob Storage.
        /// </summary>
        public async Task<string> UploadAsync(string containerName, string blobName, Stream stream)
        {
            var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
            var blobClient = containerClient.GetBlobClient(blobName);

            await blobClient.UploadAsync(stream, overwrite: true);

            return $"{containerName}/{blobName}";
        }
    }
}
