using System.Threading.Tasks;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;

namespace PrintLogApi.Services
{
    public interface IPrintImageService
    {
        // Qualified because ImplicitUsings brings System.IO into scope, which makes a bare "File"
        // ambiguous with System.IO.File. Matches how PrintImageService already spells it.
        Task<PrintImageFileDto> DownloadPrintFile(Models.File file);
    }
}
