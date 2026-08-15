#nullable enable

using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models.DTOs.Print
{
    public class ConfirmUploadRequest
    {
        [Required]
        public string? BlobPath { get; set; }

        [Required]
        [MaxLength(255)]
        public string? FileName { get; set; }

        [Range(1, 209715200)]
        public long SizeBytes { get; set; }

        [Required]
        [MaxLength(100)]
        public string? ContentType { get; set; }
    }
}
