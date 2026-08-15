#nullable enable

using PrintLogApi.Models.DTOs.Comments;
using PrintLogApi.Models.DTOs.Printer;
using System;
using System.Collections.Generic;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Models.DTOs.Print
{
    public class PrintSummaryDTO
    {

        public long Id { get; set; }

        public string? Title { get; set; }

        public PrinterSummary? Printer { get; set; }

        public DateTimeOffset? StartDate { get; set; }

        public PrintStatus Status { get; set; }

        public int? EstimatedPrintTimeInSeconds { get; set; }
        public int? PrintTimeInSeconds { get; set; }

        public ICollection<PrintFilamentSummaryDto>? FilamentUsage { get; set; }

        public int SumActualFilamentWeightMg { get; set; }

        public int SumEstimatedFilamentWeightMg { get; set; }

        public int TotalFilamentWeightMg { get; set; }


        public long CreatedByUserId { get; set; }

        public int DefaultPrintImageId { get; set; }

        public int CommentCount { get; set; }

        public Guid? ProjectId { get; set; }
        public string? ProjectName { get; set; }
    }
}
