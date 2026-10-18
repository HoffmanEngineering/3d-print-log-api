using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models;

/// <summary>
/// One product in the printer catalog. Shared by all users, so it has no owner. Facts only:
/// names, aliases, bed and nozzle specs, and where they came from. Models are never deleted,
/// only retired, because printers point at them with a NoAction foreign key.
/// </summary>
public class PrinterModel
{
    /// <summary>Stable slug, for example <c>bambu-lab-p1s</c>. A published id never changes.</summary>
    [Key]
    [MaxLength(100)]
    public string Id { get; set; } = null!;

    [Required]
    [MaxLength(50)]
    public string Make { get; set; } = null!;

    [Required]
    [MaxLength(50)]
    public string Model { get; set; } = null!;

    /// <summary>JSON array of alternative names.</summary>
    public string AliasesJson { get; set; } = "[]";

    [Required]
    public string Technology { get; set; } = null!;

    public double? BedWidthMm { get; set; }
    public double? BedDepthMm { get; set; }
    public double? BedHeightMm { get; set; }

    /// <summary>JSON array of nozzle diameters in millimeters.</summary>
    public string NozzleOptionsJson { get; set; } = "[]";

    public double? DefaultNozzleMm { get; set; }
    public double? FilamentDiameterMm { get; set; }

    public bool? HasHeatedBed { get; set; }
    public bool? HasHeatedChamber { get; set; }
    public int? ExtruderCount { get; set; }
    public int? DefaultSlotCount { get; set; }
    public string? Kinematics { get; set; }

    /// <summary>JSON object mapping each spec field to the source it came from (cura, orca or override).</summary>
    public string SourcesJson { get; set; } = "{}";

    /// <summary>Position in the featured list, or null when the model is not featured.</summary>
    public int? Featured { get; set; }

    /// <summary>Hidden models are not offered and cannot be chosen.</summary>
    public bool Hidden { get; set; }

    /// <summary>Retired models still resolve by id, but are never offered.</summary>
    public bool Retired { get; set; }

    /// <summary>Distinct recent owners, refreshed by the popularity job. Never leaves the API.</summary>
    public int OwnerCount { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>A shallow copy that EF has never seen, for the in-memory index.</summary>
    public PrinterModel DetachedCopy() => (PrinterModel)MemberwiseClone();
}
