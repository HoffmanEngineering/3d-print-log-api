using PrintLogApi.Models.DTOs.Printer;
using System;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Models.DTOs.Print
{
    public class PrintSummaryDTO
    {

        public long Id { get; set; }

        public string Title { get; set; }

        public PrinterSummary Printer { get; set; }

        public DateTimeOffset? StartDate { get; set; }

        public PrintStatus Status { get; set; }

        public long CreatedByUserId { get; set; }

        public int? DefaultPrintImageId { get; set; }
    }
}
