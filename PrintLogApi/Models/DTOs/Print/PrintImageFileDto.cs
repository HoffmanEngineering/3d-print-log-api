using System.IO;

namespace PrintLogApi.Models.DTOs.Print
{
    public class PrintImageFileDto
    {
        public string? FileName { get; set; }

        public Stream? File { get; set; }
    }
}
