#nullable enable

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models
{
    public class PrintAttachment : TimestampEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long Id { get; set; }

        public long PrintId { get; set; }
        public Print Print { get; set; } = null!;

        public Guid FileId { get; set; }
        public File File { get; set; } = null!;

        [Required]
        [MaxLength(255)]
        public string OriginalFileName { get; set; } = null!;

        [Required]
        [MaxLength(100)]
        public string ContentType { get; set; } = null!;

        public int DisplayOrder { get; set; }
    }
}
