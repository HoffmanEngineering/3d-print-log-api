using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models;

/// <summary>
/// One product in the filament catalog, imported from SpoolmanDB. Shared by all users, so it has no
/// owner. Entries are only ever soft-deleted (<see cref="RemovedAt"/>): filaments and SKU votes
/// point at them with Restrict foreign keys.
/// </summary>
public class FilamentCatalogEntry
{
    /// <summary>The SpoolmanDB filament id, for example <c>polymaker_pla_black_1000_175_n</c>.</summary>
    [Key]
    [MaxLength(200)]
    public string Id { get; set; } = null!;

    [Required]
    [MaxLength(100)]
    public string Manufacturer { get; set; } = null!;

    [Required]
    [MaxLength(255)]
    public string Name { get; set; } = null!;

    [Required]
    [MaxLength(50)]
    public string Material { get; set; } = null!;

    /// <summary>Density in g/cm³.</summary>
    public double Density { get; set; }

    /// <summary>Net filament weight in grams.</summary>
    public int WeightG { get; set; }

    /// <summary>Empty spool weight in grams, when the catalog knows it.</summary>
    public int? SpoolWeightG { get; set; }

    [MaxLength(20)]
    public string? SpoolType { get; set; }

    public double DiameterMm { get; set; }

    /// <summary>Single color as hex without <c>#</c>.</summary>
    [MaxLength(8)]
    public string? ColorHex { get; set; }

    /// <summary>Multi-color entries: a JSON array of hex strings.</summary>
    public string? ColorHexesJson { get; set; }

    public int? ExtruderTemp { get; set; }
    public int? ExtruderTempMin { get; set; }
    public int? ExtruderTempMax { get; set; }
    public int? BedTemp { get; set; }
    public int? BedTempMin { get; set; }
    public int? BedTempMax { get; set; }

    [MaxLength(30)]
    public string? Finish { get; set; }

    [MaxLength(30)]
    public string? Pattern { get; set; }

    public bool Translucent { get; set; }
    public bool Glow { get; set; }

    public DateTime ImportedAt { get; set; }

    /// <summary>Set when a later import no longer lists the entry. Never hard-deleted.</summary>
    public DateTime? RemovedAt { get; set; }
}
