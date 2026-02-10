using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models.DTOs.Printer
{
    /// <summary>
    /// Deprecated: Use PrinterSummarySimpleDto instead.
    /// This DTO includes expensive calculated fields (FilamentRemaining, FilamentLengthRemaining, etc.)
    /// that are not used by the UI and cause significant performance issues.
    /// </summary>
    [Obsolete("Use PrinterSummarySimpleDto instead. This DTO includes expensive calculations not used by the UI.", false)]
    public class PrinterSummaryWithFilamentDto : PrinterSummary
    {
        /// <summary>
        /// The collection of currently loaded filament for this printer.
        /// </summary>
        public ICollection<PrinterFilamentSummaryDto> LoadedFilaments { get; set; }
    }
}
