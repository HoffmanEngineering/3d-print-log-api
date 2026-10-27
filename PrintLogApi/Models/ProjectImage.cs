using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

public class ProjectImage : TimestampEntity, IStoredImage
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid FileId { get; set; }
    public File File { get; set; } = null!;

    /// <summary>
    /// The list-view derivative (320px WebP). Null for images uploaded before uploads were
    /// processed, and when thumbnail generation failed.
    /// </summary>
    public Guid? ThumbnailFileId { get; set; }
    public File? ThumbnailFile { get; set; }

    /// <summary>The link-preview derivative. Not generated yet (#281 fills it).</summary>
    public Guid? SocialFileId { get; set; }
    public File? SocialFile { get; set; }

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
