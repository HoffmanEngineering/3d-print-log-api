#nullable enable

using System.Threading.Tasks;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;

namespace PrintLogApi.Services
{
    public interface IPrintImageService
    {
        Task<PrintImageFileDto> DownloadPrintFile(File file);
    }
}