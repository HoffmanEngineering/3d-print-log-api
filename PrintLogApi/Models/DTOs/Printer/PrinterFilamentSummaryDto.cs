using PrintLogApi.Models.DTOs.Filament;

namespace PrintLogApi.Models.DTOs.Printer;

public class PrinterFilamentSummaryDto
{
    public Guid Id { get; set; }

    /// <summary>The 0-based slot the spool is in, or null when it was loaded without one.</summary>
    public int? Slot { get; set; }

    /// <summary>What the printer calls the slot, such as <c>T2</c>.</summary>
    public string? SlotLabel { get; set; }

    /// <summary>When the spool was loaded.</summary>
    public DateTimeOffset LoadedAt { get; set; }

    public FilamentSummaryDto? Filament { get; set; }
}
