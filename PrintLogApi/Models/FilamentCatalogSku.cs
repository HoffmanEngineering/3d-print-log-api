using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

/// <summary>
/// One user's vote that a tag SKU (a vendor product code read from a tag) is a given catalog
/// entry. A mapping is trusted once enough distinct users agree. Unique per
/// (<see cref="Format"/>, <see cref="Sku"/>, <see cref="UserId"/>).
/// </summary>
public class FilamentCatalogSku
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string Format { get; set; } = null!;

    [Required]
    [MaxLength(64)]
    public string Sku { get; set; } = null!;

    [Required]
    [MaxLength(200)]
    public string CatalogEntryId { get; set; } = null!;
    [ForeignKey("CatalogEntryId")]
    public FilamentCatalogEntry CatalogEntry { get; set; } = null!;

    public long UserId { get; set; }
    [ForeignKey("UserId")]
    public User User { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
}
