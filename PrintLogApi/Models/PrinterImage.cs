using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

/// <summary>
/// A photo attached to a printer, for fleet identification.
/// </summary>
/// <remarks>
/// <see cref="TimestampEntity.CreatedById"/> records WHO UPLOADED the image, which is not the
/// same thing as who owns it. Ownership is always <c>Printer.UserId</c>; a query filtered on
/// <c>CreatedById</c> compiles, looks right, and is wrong.
/// </remarks>
public class PrinterImage : TimestampEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public long PrinterId { get; set; }
    public Printer Printer { get; set; } = null!;

    /// <summary>The full-size image, re-encoded server-side to a canonical format.</summary>
    public Guid FileId { get; set; }
    public File File { get; set; } = null!;

    /// <summary>
    /// The list-view derivative. Null when thumbnail GENERATION failed - a failed thumbnail
    /// UPLOAD aborts the whole add and commits no row at all.
    /// </summary>
    public Guid? ThumbnailFileId { get; set; }
    public File? ThumbnailFile { get; set; }

    /// <summary>
    /// Authoritative MIME type from the server-side decode. SAS generation needs a content
    /// type and the File row does not carry a trustworthy one: the API re-encodes every
    /// upload and stores it under a generated name.
    /// </summary>
    [StringLength(64)]
    public string ContentType { get; set; } = null!;

    public bool IsDefault { get; set; }

    public int DisplayOrder { get; set; }
}
