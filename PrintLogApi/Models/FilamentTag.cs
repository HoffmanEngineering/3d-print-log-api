using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

/// <summary>
/// One RFID or NFC tag on a spool (#147). A physical tag maps to exactly one of a user's
/// filaments, so <see cref="Uid"/> is unique per <see cref="UserId"/>, not per filament.
///
/// <see cref="UserId"/> duplicates the filament's owner because the unique index needs it on
/// this row. It is written from the filament's <c>CreatedById</c> and never edited.
/// </summary>
public class FilamentTag
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public Guid FilamentId { get; set; }
    [ForeignKey("FilamentId")]
    public Filament Filament { get; set; } = null!;

    public long UserId { get; set; }
    [ForeignKey("UserId")]
    public User User { get; set; } = null!;

    /// <summary>The tag UID, normalized by <see cref="TagUid.Normalize"/>: uppercase hex, no separators.</summary>
    [Required]
    [MaxLength(TagUid.MaxLength)]
    public string Uid { get; set; } = null!;

    /// <summary>
    /// The tag's data format (<c>snapmaker</c>, <c>anycubic-ace</c>, <c>openspool</c>…), set when
    /// a read of the linked tag decodes (<c>POST /api/Filaments/tag-reads</c>). Null until then.
    /// </summary>
    [MaxLength(50)]
    public string? Format { get; set; }

    /// <summary>The last decoded reading of this tag, as JSON (at most 8 KB).</summary>
    public string? DecodedJson { get; set; }

    /// <summary>The decoder version that produced <see cref="DecodedJson"/>.</summary>
    public int? DecoderVersion { get; set; }

    public DateTime? LastReadAt { get; set; }

    public DateTime CreatedDate { get; set; }
}
