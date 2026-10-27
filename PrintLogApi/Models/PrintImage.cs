using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

public class PrintImage : TimestampEntity, IStoredImage
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public long PrintId { get; set; }

    public Print Print { get; set; } = null!;

    public Guid FileId { get; set; }
    public File File { get; set; } = null!;

    /// <summary>
    /// The list-view derivative (320px WebP). Null for images uploaded before uploads were
    /// processed, and when thumbnail generation failed.
    /// </summary>
    public Guid? ThumbnailFileId { get; set; }
    public File? ThumbnailFile { get; set; }

    /// <summary>
    /// The link-preview derivative (JPEG, about 1200px on the long edge). Null for images not yet
    /// backfilled, and when its generation failed; the social-image endpoint then serves the original.
    /// </summary>
    public Guid? SocialFileId { get; set; }
    public File? SocialFile { get; set; }

    /// <summary>Pixel width of <see cref="SocialFile"/>, for <c>og:image:width</c>.</summary>
    public int? SocialWidth { get; set; }

    /// <summary>Pixel height of <see cref="SocialFile"/>, for <c>og:image:height</c>.</summary>
    public int? SocialHeight { get; set; }

    /// <summary>
    /// MIME type of the original, from the server-side decode. Null for images uploaded before
    /// uploads were processed; those are served by file extension, raster types only.
    /// </summary>
    [StringLength(64)]
    public string? ContentType { get; set; }

    /// <summary>Pixel width of the stored original, after orientation was applied.</summary>
    public int? Width { get; set; }

    /// <summary>Pixel height of the stored original, after orientation was applied.</summary>
    public int? Height { get; set; }

    public bool IsDefault { get; set; }

    public int DisplayOrder { get; set; }
}
