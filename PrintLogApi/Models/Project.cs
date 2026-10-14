using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models;

public class Project : TimestampEntity
{
    public enum ProjectStatus
    {
        InProgress = 1,
        Complete = 2,
        OnHold = 3,
        Cancelled = 4,
    }

    public enum ProjectViewStatus
    {
        Public = 1,
        Unlisted = 2,
        Private = 3,
    }

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = null!;

    [MaxLength(100)]
    public string? Reference { get; set; }

    [MaxLength(5000)]
    public string? Description { get; set; }

    [MaxLength(1000)]
    public string? Url { get; set; }

    /// <summary>
    /// Manual start date. Null means the date is derived from the project's prints.
    /// A civil date, not an instant: project detail is anonymous for public projects,
    /// and an instant would render as different calendar days for different viewers.
    /// </summary>
    public DateOnly? StartDateOverride { get; set; }

    /// <summary>
    /// Manual finish date. Null means the date is derived from the project's prints.
    /// </summary>
    public DateOnly? FinishDateOverride { get; set; }

    public ProjectStatus Status { get; set; }

    public ProjectViewStatus ViewStatus { get; set; }

    /// <summary>
    /// Whether a caller who does not own the project may open its detail page
    /// (<c>GET /api/Projects/{id}</c>). Public and Unlisted both may; Unlisted is reachable only
    /// by someone who already has the link.
    /// </summary>
    public static bool IsViewableByNonOwner(ProjectViewStatus viewStatus) =>
        viewStatus != ProjectViewStatus.Private;

    /// <summary>
    /// Whether the project is enumerated for anonymous discovery (<c>GET /api/Projects/public</c>,
    /// the sitemap). Only Public: an Unlisted project passes <see cref="IsViewableByNonOwner"/>
    /// but listing it would defeat "only via direct link", the same line <c>/api/Prints/public</c>
    /// and <c>/api/Users/public</c> draw. Every status listed here must also pass
    /// <see cref="IsViewableByNonOwner"/>, or the sitemap would advertise a page that refuses
    /// the crawler; <c>ProjectsControllerTests</c> fetches every listed id anonymously to pin it.
    /// </summary>
    public const ProjectViewStatus PubliclyListedViewStatus = ProjectViewStatus.Public;

    public ICollection<ProjectImage>? Images { get; set; }

    public ICollection<Print>? Prints { get; set; }
}
