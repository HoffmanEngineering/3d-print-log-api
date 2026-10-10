using System.ComponentModel.DataAnnotations;
using PrintLogApi.Services;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Models.DTOs.Print;

public class AddPrintDTO : IValidatableObject
{
    public long PrinterId { get; set; }

    public int? EstimatedPrintTimeInSeconds { get; set; }
    public int? EstimatedFilamentUsageMg { get; set; }
    public int? PrintTimeInSeconds { get; set; }
    /// <summary>
    /// Filament usage in milligrams 
    /// </summary>
    public int? FilamentUsageMg { get; set; }

    [StringLength(100)]
    public string? FilamentType { get; set; }

    public ICollection<PrintFilamentSummaryDto>? FilamentUsage { get; set; }

    [StringLength(50000)]
    public string? Notes { get; set; }

    [StringLength(1000)]
    public string? Url { get; set; }

    [Required(AllowEmptyStrings = false)]
    [StringLength(100)]
    public string? Title { get; set; }

    [MaxLength(1000)]
    public string? FileName { get; set; }

    public DateTimeOffset? StartDate { get; set; }

    public PrintStatus Status { get; set; }

    public PrintViewStatus ViewStatus { get; set; }

    public bool AllowComments { get; set; }

    /// <summary>
    /// Assign to an existing project. Takes precedence over NewProjectName.
    /// </summary>
    public Guid? ProjectId { get; set; }

    /// <summary>
    /// Create a new project inline and assign this print to it. Ignored if ProjectId is set.
    /// </summary>
    [MaxLength(100)]
    public string? NewProjectName { get; set; }

    /// <summary>
    /// The <c>CuraSetting</c> this print was prefilled from, when it came through the slicer
    /// plugin. Only provenance: the slicer name and version are copied onto the print. An id that
    /// is missing (purged) or owned by someone else is ignored, never rejected.
    /// </summary>
    public Guid? CuraSettingId { get; set; }

    /// <summary>
    /// The system a connector logged this print from, e.g. <c>moonraker</c>, <c>octoprint</c>.
    /// Send together with <see cref="ExternalId"/>: a pair the caller already used returns the
    /// existing print with 200 instead of creating a duplicate.
    /// </summary>
    [StringLength(50)]
    public string? ExternalSource { get; set; }

    /// <summary>The job's identity in <see cref="ExternalSource"/>, unique per user and source.</summary>
    [StringLength(200)]
    public string? ExternalId { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ExternalSource is null && ExternalId is null)
        {
            yield break;
        }

        // Half a pair cannot dedupe anything, and a blank half would dedupe everything the
        // caller ever sends under it.
        if (string.IsNullOrWhiteSpace(ExternalSource) || string.IsNullOrWhiteSpace(ExternalId))
        {
            yield return new ValidationResult(
                "externalSource and externalId must be sent together, and neither may be blank.",
                [nameof(ExternalSource), nameof(ExternalId)]);
            yield break;
        }

        if (ExternalPrintIds.NormalizeSource(ExternalSource) == ExternalPrintIds.McpSource)
        {
            yield return new ValidationResult(
                $"externalSource '{ExternalPrintIds.McpSource}' is reserved for MCP create_print.",
                [nameof(ExternalSource)]);
        }
    }
}
