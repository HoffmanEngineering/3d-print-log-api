using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models.DTOs.Printer
{
    public class PrinterSummary
    {
        public long Id { get; set; }

        public string Make { get; set; }

        public string Model { get; set; }
    }
}
