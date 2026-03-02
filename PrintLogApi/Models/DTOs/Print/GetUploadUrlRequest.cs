using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models.DTOs.Print
{
    public class GetUploadUrlRequest
    {
        [Required]
        [MaxLength(255)]
        public string FileName { get; set; }

        [Required]
        [MaxLength(100)]
        public string ContentType { get; set; }

        [Range(1, 209715200)] // 1 byte to 200MB
        public long SizeBytes { get; set; }
    }
}
