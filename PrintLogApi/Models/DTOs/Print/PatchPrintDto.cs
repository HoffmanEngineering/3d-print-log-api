using System.ComponentModel.DataAnnotations;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Models.DTOs.Print;

/// <summary>
/// A partial update of a print. Only the fields sent change; a field left out (or sent as null)
/// is kept. To null out a nullable field, name it in <see cref="Clear"/>.
/// </summary>
public class PatchPrintDto : IValidatableObject
{
    /// <summary>The names <see cref="Clear"/> accepts, matched case-insensitively.</summary>
    public static readonly IReadOnlySet<string> ClearableFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "startDate", "estimatedPrintTimeInSeconds", "printTimeInSeconds", "notes", "url", "fileName", "projectId",
    };

    [StringLength(100)]
    public string? Title { get; set; }

    public PrintStatus? Status { get; set; }

    public DateTimeOffset? StartDate { get; set; }

    /// <summary>The printer this print ran on. Must belong to the caller.</summary>
    public long? PrinterId { get; set; }

    [Range(0, int.MaxValue, ErrorMessage = "{0} cannot be negative.")]
    public int? EstimatedPrintTimeInSeconds { get; set; }

    [Range(0, int.MaxValue, ErrorMessage = "{0} cannot be negative.")]
    public int? PrintTimeInSeconds { get; set; }

    [StringLength(50000)]
    public string? Notes { get; set; }

    [StringLength(1000)]
    public string? Url { get; set; }

    [StringLength(1000)]
    public string? FileName { get; set; }

    public bool? AllowComments { get; set; }

    public bool? AllowFileDownloads { get; set; }

    public PrintViewStatus? ViewStatus { get; set; }

    /// <summary>Assign to an existing project the caller owns.</summary>
    public Guid? ProjectId { get; set; }

    /// <summary>
    /// When sent, replaces every usage row on the print. Leave it out to keep the rows as they are.
    /// Sending it (an empty list included) also clears the legacy <c>filamentType</c>,
    /// <c>filamentUsageMg</c> and <c>estimatedFilamentUsageMg</c> fields, which the rows supersede.
    /// </summary>
    [MaxLength(CompletePrintDto.MaxFilamentUsageRows)]
    public ICollection<PutPrintFilamentSummaryDto>? FilamentUsage { get; set; }

    /// <summary>
    /// Nullable fields to null out: <c>startDate</c>, <c>estimatedPrintTimeInSeconds</c>,
    /// <c>printTimeInSeconds</c>, <c>notes</c>, <c>url</c>, <c>fileName</c>, <c>projectId</c>.
    /// Setting and clearing the same field in one request is rejected.
    /// </summary>
    public ICollection<string>? Clear { get; set; }

    /// <summary>Whether <see cref="Clear"/> names <paramref name="field"/>.</summary>
    public bool Clears(string field) => Clear?.Contains(field, StringComparer.OrdinalIgnoreCase) == true;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Title != null && string.IsNullOrWhiteSpace(Title))
        {
            yield return new ValidationResult("title cannot be blank.", [nameof(Title)]);
        }

        foreach (var field in Clear ?? [])
        {
            if (!ClearableFields.Contains(field))
            {
                yield return new ValidationResult(
                    $"'{field}' cannot be cleared. Clearable fields: {string.Join(", ", ClearableFields)}.",
                    [nameof(Clear)]);
            }
        }

        // Checked here rather than with [Range] on the row type, which PUT shares and has never
        // range-checked (#198).
        var usage = FilamentUsage ?? [];
        if (usage.Any(u => u is null))
        {
            yield return new ValidationResult("filamentUsage cannot contain null rows.", [nameof(FilamentUsage)]);
        }
        else if (usage.Any(IsOutOfRange))
        {
            yield return new ValidationResult(
                "Usage amounts, lengths, volumes and slots cannot be negative, lengths cannot exceed "
                + $"{CompletePrintDto.MaxUsageLengthInM} m and volumes {CompletePrintDto.MaxUsageVolumeMl} ml.",
                [nameof(FilamentUsage)]);
        }

        var set = new (string Field, bool IsSet)[]
        {
            ("startDate", StartDate.HasValue),
            ("estimatedPrintTimeInSeconds", EstimatedPrintTimeInSeconds.HasValue),
            ("printTimeInSeconds", PrintTimeInSeconds.HasValue),
            ("notes", Notes != null),
            ("url", Url != null),
            ("fileName", FileName != null),
            ("projectId", ProjectId.HasValue),
        };
        foreach (var (field, isSet) in set)
        {
            if (isSet && Clears(field))
            {
                yield return new ValidationResult($"'{field}' cannot be both set and cleared.", [nameof(Clear)]);
            }
        }
    }

    private static bool IsOutOfRange(PutPrintFilamentSummaryDto row) =>
        row.Slot < 0 ||
        row.AmountMg < 0 || row.EstimatedAmountMg < 0 ||
        row.LengthInM is < 0 or > CompletePrintDto.MaxUsageLengthInM ||
        row.EstimatedLengthInM is < 0 or > CompletePrintDto.MaxUsageLengthInM ||
        row.VolumeMl is < 0 or > CompletePrintDto.MaxUsageVolumeMl ||
        row.EstimatedVolumeMl is < 0 or > CompletePrintDto.MaxUsageVolumeMl;
}
