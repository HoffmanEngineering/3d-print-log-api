using PrintLogApi.Exceptions;
using PrintLogApi.Models.DTOs.Print;

namespace PrintLogApi.Services;

public class PrintImageService(IBlobStorageService blobStorage) : IPrintImageService
{
    public async Task<PrintImageFileDto> DownloadPrintFile(Models.File file)
    {
        var fileName = Path.GetFileName(file.Path);
        if (string.IsNullOrEmpty(fileName))
            throw new DoesNotExistException();

        var download = await blobStorage.DownloadAsync(BlobContainers.PrintImages, fileName)
            ?? throw new DoesNotExistException();

        return new PrintImageFileDto
        {
            File = download.stream,
            FileName = fileName
        };
    }
}
