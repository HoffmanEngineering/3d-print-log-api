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
    /// <summary>
    /// The most usage rows one completion or patch may carry (#198). Far above any real tool
    /// count, and it bounds how many rows one request can add.
    /// </summary>
    public const int MaxFilamentUsageRows = 64;

    /// <summary>
    /// The longest length one usage row may carry, in meters (#198): 100 km, about 300 kg of
    /// 1.75 mm PLA. The weight derived from it is cast to <c>int</c> milligrams, which wraps
    /// rather than throws, so an unbounded length stored a nonsense weight.
    /// </summary>
    public const double MaxUsageLengthInM = 100_000;

    /// <summary>The largest volume one usage row may carry, in milliliters: 100 L. Same reason as <see cref="MaxUsageLengthInM"/>.</summary>
    public const double MaxUsageVolumeMl = 100_000;

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
    [MaxLength(MaxFilamentUsageRows)]
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
        if (usage.Any(u => u is null))
        {
            // Every check below reads the rows, and so does the merge.
            yield return new ValidationResult("filamentUsage cannot contain null rows.", [nameof(FilamentUsage)]);
            yield break;
        }

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
    [Range(0, int.MaxValue, ErrorMessage = "{0} cannot be negative.")]
    public int? Slot { get; set; }

    /// <summary>The actual weight used in milligrams.</summary>
    [Range(0, int.MaxValue, ErrorMessage = "{0} cannot be negative.")]
    public int? AmountMg { get; set; }

    /// <summary>The actual length used in meters.</summary>
    [Range(0d, CompletePrintDto.MaxUsageLengthInM, ErrorMessage = "{0} must be between {1} and {2}.")]
    public double? LengthInM { get; set; }

    /// <summary>The actual volume used in milliliters.</summary>
    [Range(0d, CompletePrintDto.MaxUsageVolumeMl, ErrorMessage = "{0} must be between {1} and {2}.")]
    public double? VolumeMl { get; set; }

    /// <summary>
    /// Which of the three values is the measurement. Defaults to the first one sent, in the order
    /// weight, length, volume.
    /// </summary>
    public SourceMeasurement? Source { get; set; }

    /// <summary>
    /// True when these figures are the slicer's estimate, not a measurement: send it from a
    /// printer that reports only the estimate (most firmwares other than Klipper). The spool is
    /// still charged, but accuracy analytics leave the row out. Absent or null means measured.
    /// </summary>
    public bool? ActualIsEstimate { get; set; }

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
