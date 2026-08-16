using System;
using System.Collections.Generic;
using PrintLogApi.Models.DTOs.Print;
using static PrintLogApi.Models.Project;

namespace PrintLogApi.Models.DTOs.Project
{
    public class ProjectDetailDto
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public string? Reference { get; set; }
        public string? Description { get; set; }
        public string? Url { get; set; }
        public ProjectStatus Status { get; set; }
        public ProjectViewStatus ViewStatus { get; set; }
        public DateTimeOffset CreatedDate { get; set; }
        public long CreatedByUserId { get; set; }
        public int PrintCount { get; set; }
        public int TotalPrintTimeInSeconds { get; set; }
        public int TotalEstimatedPrintTimeInSeconds { get; set; }
        public long TotalFilamentWeightMg { get; set; }
        public IList<ProjectImageDto>? Images { get; set; }
    }
}
