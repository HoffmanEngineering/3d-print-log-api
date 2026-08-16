using Azure.Storage.Blobs;
using PrintLogApi.Exceptions;
using PrintLogApi.Models.DTOs.Print;

namespace PrintLogApi.Services;

public class PrintImageService : IPrintImageService
{
    private readonly string printImageContainerName = "printimages";
    private readonly BlobContainerClient printImageContainer;

    public PrintImageService(IConfiguration config)
    {
        var blobServiceClient = new BlobServiceClient(config["AZURE_STORAGE_CONNECTION_STRING"]);
        printImageContainer = blobServiceClient.GetBlobContainerClient(printImageContainerName);
    }

    public async Task<PrintImageFileDto> DownloadPrintFile(Models.File file)
    {
        var fileName = Path.GetFileName(file.Path);
        var blobClient = printImageContainer.GetBlobClient(fileName);

        if (await blobClient.ExistsAsync())
        {
            var ms = new MemoryStream();
            var stream = await blobClient.DownloadToAsync(ms);
            ms.Position = 0;

            return new PrintImageFileDto()
            {
                File = ms,
                FileName = fileName
            };

        }
        else
        {
            throw new DoesNotExistException();
        }
    }

}
