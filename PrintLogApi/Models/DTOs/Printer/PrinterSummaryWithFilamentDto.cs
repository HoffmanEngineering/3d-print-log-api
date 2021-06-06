using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models.DTOs.Printer
{
    public class PrinterSummaryWithFilamentDto : PrinterSummary
    {
        /// <summary>
        /// The collection of currently loaded filament for this printer.
        /// </summary>
        public ICollection<PrinterFilamentSummaryDto> LoadedFilaments { get; set; }
    }
}
