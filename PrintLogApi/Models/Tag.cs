using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

/// <summary>
/// A user's label for prints (UI #7). Many-to-many with <see cref="Print"/> through
/// <see cref="PrintTag"/>, so it complements <see cref="Project"/>, which a print belongs to at
/// most one of. Owner-only: tags never appear on another user's view of a print.
/// </summary>
/// <remarks>
/// Not to be confused with <see cref="FilamentTag"/>, which is a physical NFC/RFID tag on a spool.
/// </remarks>
public class Tag : TimestampEntity
{
    public const int NameMaxLength = 50;

    /// <summary>How many tags one print may carry. A label, not a keyword dump.</summary>
    public const int MaxTagsPerPrint = 20;

    /// <summary>How many tags one user may own. Bounds the stats query and the autocomplete list.</summary>
    public const int MaxTagsPerUser = 200;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(NameMaxLength)]
    public string Name { get; set; } = null!;

    /// <summary>
    /// <see cref="Name"/> trimmed and upper-cased invariantly. The unique index is on this column,
    /// not on <see cref="Name"/>, so "Gifts" and "gifts" collide on every provider: SQL Server's
    /// default collation is case-insensitive but SQLite's is not, and the suite runs on SQLite.
    /// </summary>
    [Required]
    [MaxLength(NameMaxLength)]
    public string NormalizedName { get; set; } = null!;

    /// <summary>A <c>#rrggbb</c> colour, lower-cased, or null for the default chip colour.</summary>
    [MaxLength(7)]
    public string? Color { get; set; }

    public ICollection<PrintTag>? PrintTags { get; set; }

    public static string Normalize(string name) => name.Trim().ToUpperInvariant();
}
