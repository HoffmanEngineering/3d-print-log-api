using PrintLogApi.Services;

namespace PrintLogApi.Models.DTOs.Print;

public class PrintImageDto
{
    public int Id { get; set; }

    /// <summary>The print the image belongs to.</summary>
    public long PrintId { get; set; }

    public bool IsDefault { get; set; }

    public int DisplayOrder { get; set; }

    /// <summary>
    /// Path of the image's thumbnail (320px WebP, or the original for an image without one),
    /// relative to the API base URL: <c>/api/Prints/{printId}/image/{id}/thumbnail</c>.
    /// </summary>
    public string? ThumbnailUrl => ImageUrls.PrintThumbnail(PrintId, Id);
}
