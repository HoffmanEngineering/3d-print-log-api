using System.Collections.Generic;
using PrintLogApi.Models.DTOs.PrinterCategory;

namespace PrintLogApi.Models.DTOs.Printer
{
    /// <summary>
    /// Simplified printer summary for list displays.
    /// Uses lightweight filament DTOs to avoid expensive query calculations.
    /// </summary>
    public class PrinterSummarySimpleDto
    {
        public long Id { get; set; }

        public string? Name { get; set; }

        public string? Make { get; set; }

        public string? Model { get; set; }

        public bool IsActive { get; set; }

        public double? WattageW { get; set; }

        public PrinterCategoryDto? Category { get; set; }

        public ICollection<PrinterFilamentForSummaryDto>? LoadedFilaments { get; set; }
    }
}
