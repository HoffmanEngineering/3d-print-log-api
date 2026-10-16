using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

/// <summary>
/// A tag read the decoders could not make sense of, kept so new formats can be added from real
/// dumps. Deliberately has no user FK: it holds no account data and is pruned by age (90 days).
/// </summary>
public class TagReadFailure
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public DateTime CreatedAt { get; set; }

    [Required]
    [MaxLength(TagUid.MaxLength)]
    public string Uid { get; set; } = null!;

    /// <summary>The NFC technologies the tag reported.</summary>
    [MaxLength(200)]
    public string? Techs { get; set; }

    /// <summary>Which kind of memory <see cref="Data"/> holds: <c>classic</c>, <c>type2</c> or <c>nfcv</c>.</summary>
    [Required]
    [MaxLength(10)]
    public string MemoryKind { get; set; } = null!;

    [Required]
    [MaxLength(2048)]
    public byte[] Data { get; set; } = null!;

    [MaxLength(50)]
    public string? DetectedFormat { get; set; }

    [Required]
    [MaxLength(500)]
    public string Error { get; set; } = null!;
}
