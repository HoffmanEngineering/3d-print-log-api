using System;
using System.Collections.Generic;
using PrintLogApi.Models.DTOs.Printer;
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
        public string? Type { get; set; }

        /// <summary>Used for chronological sort across both types.</summary>
        public DateTimeOffset SortDate { get; set; }

        // --- Project fields (populated when Type == "project") ---
        public Guid? ProjectId { get; set; }
        public string? ProjectName { get; set; }
        public string? ProjectReference { get; set; }
        public ProjectStatus? ProjectStatus { get; set; }

        /// <summary>Total prints in this project (unfiltered).</summary>
        public int? PrintCount { get; set; }

        /// <summary>
        /// Prints in this project matching the current filters.
        /// Null when no filters are active (all prints match).
        /// </summary>
        public int? FilteredPrintCount { get; set; }

        public int? TotalPrintTimeInSeconds { get; set; }
        public int? TotalEstimatedPrintTimeInSeconds { get; set; }
        public long? TotalFilamentWeightMg { get; set; }
        public int? DefaultProjectImageId { get; set; }

        /// <summary>
        /// Aggregated filament usage across all prints in this project,
        /// grouped by FilamentId with weights summed.
        /// </summary>
        public ICollection<PrintFilamentSummaryDto>? FilamentUsage { get; set; }

        /// <summary>Distinct printers used across all prints in this project.</summary>
        public ICollection<PrinterSummary>? Printers { get; set; }

        // --- Print fields (populated when Type == "print") ---
        public PrintSummaryDTO? Print { get; set; }
    }
}
