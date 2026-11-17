using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

/// <summary>
/// Stores a link between a printer and loaded filament.
/// </summary>
public class PrinterFilament
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public long PrinterId { get; set; }

    public Printer Printer { get; set; } = null!;

    public Guid FilamentId { get; set; }

    public Filament Filament { get; set; } = null!;

    /// <summary>
    /// When the filament was loaded.
    /// </summary>
    public DateTimeOffset LoadedDateTime { get; set; }

    /// <summary>
    /// When the filament was unloaded from the machine.
    /// </summary>
    public DateTimeOffset? UnloadedDateTime { get; set; }

    /// <summary>
    /// The 0-based position the spool is loaded in (tool, AMS slot), or null on a printer that
    /// was loaded without slots.
    /// </summary>
    public int? Slot { get; set; }

    /// <summary>What the printer calls the slot, such as <c>T2</c> or <c>AMS A3</c>.</summary>
    [MaxLength(MaxSlotLabelLength)]
    public string? SlotLabel { get; set; }

    public const int MaxSlotLabelLength = 20;
}
