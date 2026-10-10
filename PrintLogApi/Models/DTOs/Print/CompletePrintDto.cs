using System.ComponentModel.DataAnnotations;
using static PrintLogApi.Models.Print;
using static PrintLogApi.Models.PrintFilament;

namespace PrintLogApi.Models.DTOs.Print;

/// <summary>
/// What a connector knows when a job ends. Everything else on the print, including edits the
/// user made while it ran, is left alone.
/// </summary>
public class CompletePrintDto : IValidatableObject
{
    /// <summary>The final status: Success, Cancelled, Failed or PartialSuccess.</summary>
    public PrintStatus Status { get; set; }

    /// <summary>When the job ended. Fills in the start date or the duration when one is missing.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>The measured print duration in seconds.</summary>
    public int? PrintTimeInSeconds { get; set; }

    /// <summary>
    /// Actual usage, merged into the print's rows by filament: actual values are set, estimates
    /// and notes are kept. Rows not mentioned here are left as they are.
    /// </summary>
    public ICollection<CompletePrintFilamentUsageDto>? FilamentUsage { get; set; }

    /// <summary>The statuses a job can end in.</summary>
    public static bool IsFinished(PrintStatus status) =>
        status is PrintStatus.Success or PrintStatus.Cancelled or PrintStatus.Failed or PrintStatus.PartialSuccess;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!IsFinished(Status))
        {
            yield return new ValidationResult(
                "status must be a finished status: Success (3), Cancelled (4), Failed (5) or PartialSuccess (6).",
                [nameof(Status)]);
        }

        if (PrintTimeInSeconds < 0)
        {
            yield return new ValidationResult("printTimeInSeconds cannot be negative.", [nameof(PrintTimeInSeconds)]);
        }

        var usage = FilamentUsage ?? [];
        var linked = usage.Where(u => u.FilamentId.HasValue).Select(u => u.FilamentId).ToList();
        if (linked.Count != linked.Distinct().Count())
        {
            // A row is matched to the print by filament, so two rows for one spool are ambiguous.
            yield return new ValidationResult("Each filamentId may appear at most once.", [nameof(FilamentUsage)]);
        }

        var unlinkedSlots = usage.Where(u => !u.FilamentId.HasValue && u.Slot.HasValue).Select(u => u.Slot).ToList();
        if (unlinkedSlots.Count != unlinkedSlots.Distinct().Count())
        {
            // A row with no filament is matched to the print by slot, so two for one slot are ambiguous.
            yield return new ValidationResult("Each slot may appear at most once among rows with no filamentId.", [nameof(FilamentUsage)]);
        }

        if (usage.Any(u => u.ResolveSource() is null))
        {
            yield return new ValidationResult(
                "Each usage row needs amountMg, lengthInM or volumeMl, and source must name one that was sent.",
                [nameof(FilamentUsage)]);
        }
    }
}

/// <summary>One material's actual usage for <see cref="CompletePrintDto"/>.</summary>
public class CompletePrintFilamentUsageDto
{
    /// <summary>The filament used, or null when no spool is linked.</summary>
    public Guid? FilamentId { get; set; }

    /// <summary>
    /// The printer slot (tool) the material was fed from. A row with no filament is matched to
    /// the print's row for this slot.
    /// </summary>
    public int? Slot { get; set; }

    /// <summary>The actual weight used in milligrams.</summary>
    public int? AmountMg { get; set; }

    /// <summary>The actual length used in meters.</summary>
    public double? LengthInM { get; set; }

    /// <summary>The actual volume used in milliliters.</summary>
    public double? VolumeMl { get; set; }

    /// <summary>
    /// Which of the three values is the measurement. Defaults to the first one sent, in the order
    /// weight, length, volume.
    /// </summary>
    public SourceMeasurement? Source { get; set; }

    /// <summary>The measurement this row carries, or null when it carries none (or not the one named).</summary>
    public SourceMeasurement? ResolveSource()
    {
        bool Has(SourceMeasurement m) => m switch
        {
            SourceMeasurement.Weight => AmountMg.HasValue,
            SourceMeasurement.Length => LengthInM.HasValue,
            SourceMeasurement.Volume => VolumeMl.HasValue,
            _ => false,
        };

        if (Source.HasValue)
        {
            return Has(Source.Value) ? Source : null;
        }
        return new[] { SourceMeasurement.Weight, SourceMeasurement.Length, SourceMeasurement.Volume }
            .Cast<SourceMeasurement?>()
            .FirstOrDefault(m => Has(m!.Value));
    }
}
