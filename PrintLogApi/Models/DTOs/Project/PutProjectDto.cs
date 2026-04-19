using System;
using System.ComponentModel.DataAnnotations;
using static PrintLogApi.Models.Project;

namespace PrintLogApi.Models.DTOs.Project
{
    public class PutProjectDto
    {
        [Required]
        public Guid Id { get; set; }

        [Required]
        [MaxLength(100)]
        public string Name { get; set; }

        [MaxLength(100)]
        public string Reference { get; set; }

        [MaxLength(5000)]
        public string Description { get; set; }

        [MaxLength(1000)]
        public string Url { get; set; }

        public ProjectStatus Status { get; set; }

        public ProjectViewStatus ViewStatus { get; set; }
    }
}
