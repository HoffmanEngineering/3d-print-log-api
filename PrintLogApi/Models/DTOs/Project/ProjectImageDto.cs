using PrintLogApi.Services;

namespace PrintLogApi.Models.DTOs.Project;

public class ProjectImageDto
{
    public int Id { get; set; }

    /// <summary>The project the image belongs to.</summary>
    public Guid ProjectId { get; set; }

    public bool IsDefault { get; set; }
    public int DisplayOrder { get; set; }
    public string? Url { get; set; }

    /// <summary>
    /// Path of the image's thumbnail (320px WebP, or the original for an image without one),
    /// relative to the API base URL: <c>/api/Projects/{projectId}/images/{id}/thumbnail</c>.
    /// </summary>
    public string? ThumbnailUrl => ImageUrls.ProjectThumbnail(ProjectId, Id);
}
