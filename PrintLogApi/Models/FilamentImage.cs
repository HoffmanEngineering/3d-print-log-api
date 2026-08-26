using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

public class FilamentImage : TimestampEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public Guid FilamentId { get; set; }
    public Filament Filament { get; set; } = null!;

    /// <summary>The full-size image, re-encoded server-side to a canonical format.</summary>
    public Guid FileId { get; set; }
    public File File { get; set; } = null!;

    /// <summary>The list-view derivative. Null only if the thumbnail upload failed.</summary>
    public Guid? ThumbnailFileId { get; set; }
    public File? ThumbnailFile { get; set; }

    /// <summary>
    /// Authoritative MIME type from the server-side decode. Stored because SAS
    /// generation needs a content type and neither the File row nor the blob
    /// extension is trustworthy: the client resizer emits JPEG bytes while
    /// preserving the original filename, so a ".png" blob routinely holds JPEG.
    /// </summary>
    [StringLength(64)]
    public string ContentType { get; set; } = null!;

    public bool IsDefault { get; set; }

    public int DisplayOrder { get; set; }
}
