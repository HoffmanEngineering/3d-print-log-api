using PrintLogApi.Models.DTOs.PrinterCategory;

namespace PrintLogApi.Models.DTOs.Printer;

public class PrinterSummary
{
    public long Id { get; set; }

    public string? Name { get; set; }

    public string? Make { get; set; }

    public string? Model { get; set; }

    public bool IsActive { get; set; }

    public double? WattageW { get; set; }

    public long? PrintTimeInSeconds { get; set; }

    public PrinterCategoryDto? Category { get; set; }
}
