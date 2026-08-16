using PrintLogApi.Models.DTOs.Filament;

namespace PrintLogApi.Models.DTOs.Printer;

public class PrinterFilamentSummaryDto
{
    public Guid Id { get; set; }

    public FilamentSummaryDto? Filament { get; set; }
}
