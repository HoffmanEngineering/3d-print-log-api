using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models.DTOs.Printer;

/// <summary>The spool to load into one of a printer's slots.</summary>
public class LoadPrinterSlotDto
{
    /// <summary>The spool to load. It is unloaded from wherever else it is loaded.</summary>
    [Required]
    public Guid? FilamentId { get; set; }

    /// <summary>What the printer calls the slot, such as <c>T2</c> or <c>AMS A3</c>.</summary>
    [MaxLength(Models.PrinterFilament.MaxSlotLabelLength)]
    public string? SlotLabel { get; set; }
}
