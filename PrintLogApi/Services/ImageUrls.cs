namespace PrintLogApi.Services;

/// <summary>
/// The paths of the print and project image endpoints, relative to the API's base URL. DTOs
/// carry these as-is, and the web app prefixes its API base URL, as it already does when it
/// builds <c>/api/Prints/{id}/image/{imageId}</c> from an image id.
/// </summary>
public static class ImageUrls
{
    public static string PrintImage(long printId, int imageId) => $"/api/Prints/{printId}/image/{imageId}";

    public static string PrintThumbnail(long printId, int imageId) => $"{PrintImage(printId, imageId)}/thumbnail";

    public static string PrintSocialImage(long printId) => $"/api/Prints/{printId}/social-image";

    public static string ProjectImage(Guid projectId, int imageId) => $"/api/Projects/{projectId}/images/{imageId}";

    public static string ProjectThumbnail(Guid projectId, int imageId) => $"{ProjectImage(projectId, imageId)}/thumbnail";

    public static string ProjectSocialImage(Guid projectId) => $"/api/Projects/{projectId}/social-image";

    /// <summary>The thumbnail of a default image id, or null for 0 (no default image).</summary>
    public static string? PrintThumbnailOrNull(long printId, int imageId)
        => imageId > 0 ? PrintThumbnail(printId, imageId) : null;

    /// <inheritdoc cref="PrintThumbnailOrNull"/>
    public static string? ProjectThumbnailOrNull(Guid? projectId, int? imageId)
        => projectId is { } id && imageId is > 0 ? ProjectThumbnail(id, imageId.Value) : null;
}
