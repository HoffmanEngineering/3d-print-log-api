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

    public ICollection<ProjectImage>? Images { get; set; }

    public ICollection<Print>? Prints { get; set; }
}
