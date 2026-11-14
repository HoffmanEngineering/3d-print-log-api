using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PrintLogApi.SpoolTags.Labels;

namespace PrintLogApi.Models;

/// <summary>
/// A barcode printed on a spool's label or box, kept so a later scan finds the spool. A barcode
/// names a product, not one spool, so several of a user's spools may share a value. Unique per
/// (<see cref="FilamentId"/>, <see cref="Value"/>).
/// </summary>
public class FilamentBarcode
{
    /// <summary>A spool keeps at most this many; remembering another drops the oldest.</summary>
    public const int MaxPerFilament = 4;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public Guid FilamentId { get; set; }
    [ForeignKey("FilamentId")]
    public Filament Filament { get; set; } = null!;

    /// <summary>Normalized by <see cref="BarcodeValue.Normalize"/>.</summary>
    [Required]
    [MaxLength(BarcodeValue.MaxLength)]
    public string Value { get; set; } = null!;

    /// <summary>The symbology, from <see cref="BarcodeValue.NormalizeFormat"/> (<c>ean13</c>, <c>code128</c>…).</summary>
    [Required]
    [MaxLength(BarcodeValue.FormatMaxLength)]
    public string Format { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
}
