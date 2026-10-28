using PrintLogApi.Services;
using static PrintLogApi.Models.Project;

namespace PrintLogApi.Models.DTOs.Project;

public class ProjectSummaryDto : IProjectDates
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public string? Reference { get; set; }
    public ProjectStatus Status { get; set; }
    public ProjectViewStatus ViewStatus { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
    public int PrintCount { get; set; }
    public int TotalPrintTimeInSeconds { get; set; }
    public int TotalEstimatedPrintTimeInSeconds { get; set; }
    public long TotalFilamentWeightMg { get; set; }

    /// <summary>
    /// Resin used, in milliliters (#294): only usage recorded as a volume, summed by the same
    /// actual-else-estimate rule as the gram total. Beside TotalFilamentWeightMg, never added to it.
    /// </summary>
    public double TotalFilamentVolumeMl { get; set; }
    public int DefaultImageId { get; set; }

    /// <summary>
    /// Path of the default image's thumbnail, relative to the API base URL. Null when the project
    /// has no default image (<see cref="DefaultImageId"/> is 0).
    /// </summary>
    public string? DefaultImageThumbnailUrl => ImageUrls.ProjectThumbnailOrNull(Id, DefaultImageId);

    /// <inheritdoc />
    public DateOnly StartDate { get; set; }

    /// <inheritdoc />
    public DateOnly? FinishDate { get; set; }

    /// <inheritdoc />
    public DateOnly? StartDateOverride { get; set; }

    /// <inheritdoc />
    public DateOnly? FinishDateOverride { get; set; }
}
