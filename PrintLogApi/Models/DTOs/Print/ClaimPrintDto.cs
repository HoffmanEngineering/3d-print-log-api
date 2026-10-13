using System.ComponentModel.DataAnnotations;
using PrintLogApi.Services;

namespace PrintLogApi.Models.DTOs.Print;

/// <summary>
/// A connector taking over a print something else created (#165): the slicer uploader, the Cura
/// plugin or a hand entry starts the print, and the connector only reports how it ends.
/// </summary>
public class ClaimPrintDto : IValidatableObject
{
    /// <summary>The connector claiming the print, e.g. <c>moonraker</c>. Same rules as on create.</summary>
    [StringLength(50)]
    public string? ExternalSource { get; set; }

    /// <summary>The job's identity in <see cref="ExternalSource"/>.</summary>
    [StringLength(200)]
    public string? ExternalId { get; set; }

    /// <summary>
    /// The <c>instanceId</c> of the claiming connection, so the print shows "Logged automatically
    /// by" it. An id that is not one of yours is ignored, never rejected.
    /// </summary>
    [StringLength(100)]
    public string? ConnectionInstanceId { get; set; }

    /// <summary>When the job started. Applied only when the print has no start date yet.</summary>
    public DateTimeOffset? StartDate { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // Same as AddPrintDTO, except that the pair is the whole point of a claim, so it is required.
        if (string.IsNullOrWhiteSpace(ExternalSource) || string.IsNullOrWhiteSpace(ExternalId))
        {
            yield return new ValidationResult(
                "externalSource and externalId are both required, and neither may be blank.",
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
