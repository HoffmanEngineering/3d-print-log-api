using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Models.DTOs.Print
{
    public class AddPrintDTO
    {
        public long PrinterId { get; set; }

        public int? EstimatedPrintTimeInSeconds { get; set; }
        public int? EstimatedFilamentUsageMg { get; set; }
        public int? PrintTimeInSeconds { get; set; }
        /// <summary>
        /// Filament usage in milligrams 
        /// </summary>
        public int? FilamentUsageMg { get; set; }

        [StringLength(100)]
        public string FilamentType { get; set; }

        public ICollection<PrintFilamentSummaryDto> FilamentUsage { get; set; }

        [StringLength(50000)]
        public string Notes { get; set; }

        [StringLength(1000)]
        public string Url { get; set; }

        [Required(AllowEmptyStrings = false)]
        [StringLength(100)]
        public string Title { get; set; }

        [MaxLength(1000)]
        public string FileName { get; set; }

        public DateTimeOffset? StartDate { get; set; }

        public PrintStatus Status { get; set; }

        public PrintViewStatus ViewStatus { get; set; }

        public bool AllowComments { get; set; }

        /// <summary>
        /// Assign to an existing project. Takes precedence over NewProjectName.
        /// </summary>
        public Guid? ProjectId { get; set; }

        /// <summary>
        /// Create a new project inline and assign this print to it. Ignored if ProjectId is set.
        /// </summary>
        [MaxLength(100)]
        public string NewProjectName { get; set; }
    }
}
