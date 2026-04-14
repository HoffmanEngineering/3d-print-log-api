using System;
using PrintLogApi.Models.DTOs.Project;
using static PrintLogApi.Models.Project;

namespace PrintLogApi.Models.DTOs.Print
{
    /// <summary>
    /// A single item in the grouped/interleaved print feed.
    /// Type discriminator: "project" or "print".
    /// </summary>
    public class GroupedFeedItemDto
    {
        /// <summary>"project" or "print"</summary>
        public string Type { get; set; }

        /// <summary>Used for chronological sort across both types.</summary>
        public DateTimeOffset SortDate { get; set; }

        // --- Project fields (populated when Type == "project") ---
        public Guid? ProjectId { get; set; }
        public string ProjectName { get; set; }
        public string ProjectReference { get; set; }
        public ProjectStatus? ProjectStatus { get; set; }
        public int? PrintCount { get; set; }
        public int? TotalPrintTimeInSeconds { get; set; }
        public int? TotalEstimatedPrintTimeInSeconds { get; set; }
        public long? TotalFilamentWeightMg { get; set; }
        public int? DefaultProjectImageId { get; set; }

        // --- Print fields (populated when Type == "print") ---
        public PrintSummaryDTO Print { get; set; }
    }
}
