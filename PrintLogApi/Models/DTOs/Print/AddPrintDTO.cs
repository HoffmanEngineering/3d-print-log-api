using System;
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

        [StringLength(1000)]
        public string Notes { get; set; }

        [StringLength(1000)]
        public string Url { get; set; }

        [Required(AllowEmptyStrings = false)]
        [StringLength(100)]
        public string Title { get; set; }

        public DateTimeOffset? StartDate { get; set; }

        public PrintStatus Status { get; set; }
    }
}
