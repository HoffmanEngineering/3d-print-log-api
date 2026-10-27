namespace PrintLogApi.Models;

/// <summary>
/// The columns print and project images share for a processed upload: the original, its
/// derivatives, and what the decode found. Lets the image backfill treat both tables alike.
/// </summary>
public interface IStoredImage
{
    int Id { get; }
    long CreatedById { get; }

    Guid FileId { get; }
    File File { get; set; }

    Guid? ThumbnailFileId { get; }
    File? ThumbnailFile { get; set; }

    Guid? SocialFileId { get; }
    File? SocialFile { get; set; }

    string? ContentType { get; set; }
    int? Width { get; set; }
    int? Height { get; set; }
}
