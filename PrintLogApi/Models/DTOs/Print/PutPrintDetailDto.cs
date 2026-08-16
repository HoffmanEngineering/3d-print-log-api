using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Models.DTOs.Print
{
    /// <summary>
    /// DTO for updating a print's detailed information.
    /// </summary>
    public class PutPrintDetailDto
    {
        /// <summary>
        /// The Print's ID
        /// </summary>
        public long Id { get; set; }

        /// <summary>
        /// The title of the print.
        /// </summary>
        [Required(AllowEmptyStrings = false)]
        [StringLength(100)]
        public string? Title { get; set; }

        public DateTimeOffset? StartDate { get; set; }

        /// <summary>
        /// The printer id of the printer this print was printed on.
        /// </summary>
        public long PrinterId { get; set; }

        /// <summary>
        /// The estimated print duration in seconds.
        /// </summary>
        public int? EstimatedPrintTimeInSeconds { get; set; }
        /// <summary>
        /// The non-tracked estimated Filament usage in milligrams. Used before the FilamentUsage collection was added.
        /// </summary>
        [Obsolete("EstimatedFilamentUsageMg is deprecated, use the FilamentUsage collection instead.")]
        public int? EstimatedFilamentUsageMg { get; set; }

        /// <summary>
        /// The actual print duration in seconds.
        /// </summary>
        public int? PrintTimeInSeconds { get; set; }

        /// <summary>
        /// The non-tracked Filament usage in milligrams. Used before the FilamentUsage collection was added.
        /// </summary>
        [Obsolete("FilamentUsageMg is deprecated, use the FilamentUsage collection instead.")]
        public int? FilamentUsageMg { get; set; }

        /// <summary>
        /// The non-tracked Filament Type. Used before the FilamentUsage collection was added.
        /// </summary>
        [Obsolete("FilamentType is deprecated, use the FilamentUsage collection instead.")]
        public string? FilamentType { get; set; }

        /// <summary>
        /// The collection of Filament Usage information.
        /// </summary>
        public ICollection<PutPrintFilamentSummaryDto>? FilamentUsage { get; set; }

        [StringLength(50000)]
        public string? Notes { get; set; }

        [StringLength(1000)]
        public string? Url { get; set; }

        [MaxLength(1000)]
        public string? FileName { get; set; }

        /// <summary>
        /// Whether or not comments are allowed on this print.
        /// </summary>
        public bool AllowComments { get; set; }

        /// <summary>
        /// Whether or not file downloads are allowed on this print.
        /// </summary>
        public bool AllowFileDownloads { get; set; }

        public PrintStatus Status { get; set; }

        public PrintViewStatus ViewStatus { get; set; }

        /// <summary>
        /// Assign to an existing project. Takes precedence over NewProjectName.
        /// </summary>
        public Guid? ProjectId { get; set; }

        /// <summary>
        /// Create a new project inline and assign this print to it. Ignored if ProjectId is set.
        /// </summary>
        [MaxLength(100)]
        public string? NewProjectName { get; set; }

    }
}
