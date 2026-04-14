using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models
{
    public class ProjectImage : TimestampEntity
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        public Guid ProjectId { get; set; }
        public Project Project { get; set; }

        public Guid FileId { get; set; }
        public File File { get; set; }

        public bool IsDefault { get; set; }

        public int DisplayOrder { get; set; }
    }
}
