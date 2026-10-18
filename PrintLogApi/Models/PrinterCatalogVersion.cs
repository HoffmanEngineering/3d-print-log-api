using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

/// <summary>
/// The single row recording which <c>printer-catalog.json</c> version was last seeded into
/// <see cref="PrinterModel"/>. The seeder skips the upsert when the embedded file's version matches.
/// </summary>
public class PrinterCatalogVersion
{
    /// <summary>The only row's key.</summary>
    public const int SingletonId = 1;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Id { get; set; } = SingletonId;

    /// <summary>The catalog file's <c>version</c>: a SHA-256 of its models, lowercase hex.</summary>
    [Required]
    [MaxLength(64)]
    public string Version { get; set; } = null!;

    /// <summary>
    /// The seeded file's <c>generatedAt</c>. The seeder skips an embedded file older than this, so a
    /// rollback deploy does not re-seed last month's catalog. Null on rows from before it existed.
    /// </summary>
    public DateTimeOffset? CatalogGeneratedAt { get; set; }

    public DateTimeOffset SeededAt { get; set; }
}
