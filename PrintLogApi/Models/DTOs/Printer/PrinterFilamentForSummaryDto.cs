using System;
using PrintLogApi.Models.DTOs.Filament;

namespace PrintLogApi.Models.DTOs.Printer
{
    /// <summary>
    /// Lightweight printer-filament relationship for summary views.
    /// Uses FilamentSummaryForPrinterDto to avoid expensive calculations.
    /// </summary>
    public class PrinterFilamentForSummaryDto
    {
        public Guid Id { get; set; }

        public FilamentSummaryForPrinterDto? Filament { get; set; }
    }
}
