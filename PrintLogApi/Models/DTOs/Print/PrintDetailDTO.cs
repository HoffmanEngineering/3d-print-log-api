using PrintLogApi.Models.DTOs.Comments;
using PrintLogApi.Models.DTOs.Printer;
using System;
using System.Collections.Generic;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Models.DTOs.Print
{
    public class PrintDetailDTO
    {
        public long Id { get; set; }

        public string Title { get; set; }

        public DateTimeOffset? StartDate { get; set; }

        public long PrinterId { get; set; }

        public PrinterSummary Printer { get; set; }

        public int? EstimatedPrintTimeInSeconds { get; set; }
        public int? EstimatedFilamentUsageMg { get; set; }
        public int? PrintTimeInSeconds { get; set; }
        /// <summary>
        /// Filament usage in milligrams 
        /// </summary>
        public int? FilamentUsageMg { get; set; }

        public string FilamentType { get; set; }

        public ICollection<PrintFilamentSummaryDto> FilamentUsage { get; set; }

        public string Notes { get; set; }

        public string Url { get; set; }

        public long CreatedByUserId { get; set; }

        public bool AllowComments { get; set; }

        public PrintStatus Status { get; set; }

        public PrintViewStatus ViewStatus { get; set; }

        public ICollection<PrintImageDto> Images { get; set; }

        public ICollection<CommentDetailDto> Comments { get; set; }
    }
}
