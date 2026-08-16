using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PrintLogApi.Models.DTOs.PrinterCategory;

namespace PrintLogApi.Models.DTOs.Printer
{
    public class PrinterSummaryWithoutCategory
    {
        public long Id { get; set; }

        public string? Name { get; set; }

        public string? Make { get; set; }

        public string? Model { get; set; }

        public bool IsActive { get; set; }
    }
}
