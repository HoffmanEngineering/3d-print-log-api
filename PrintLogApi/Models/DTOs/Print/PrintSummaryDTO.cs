using PrintLogApi.Models.DTOs.Printer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Models.DTOs.Print
{
    public class PrintSummaryDTO
    {

        public long Id { get; set; }

        public string Title { get; set; }

        public PrinterSummary printer { get; set; }

        public DateTimeOffset? StartDate { get; set; }

        public PrintStatus Status { get; set; }
    }
}
