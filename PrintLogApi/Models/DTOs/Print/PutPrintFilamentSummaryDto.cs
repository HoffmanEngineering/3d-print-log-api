using static PrintLogApi.Models.PrintFilament;

namespace PrintLogApi.Models.DTOs.Print;

public class PutPrintFilamentSummaryDto
{
    /// <summary>
    /// The GUID of the PrintFilament collection. Use EMPTY_GUID for new entries.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The optional GUID of the filament used. Set as null (or EMPTY_GUID) to not 
    /// link this usage to a filament, and instead treat this as a non-tracked filament.
    /// </summary>
    public Guid? FilamentId { get; set; }

    /// <summary>
    /// The estimated weight of filament used in milligrams.
    /// </summary>
    public int? EstimatedAmountMg { get; set; }

    /// <summary>
    /// The actual weight of filament used in milligrams.
    /// </summary>
    public int? AmountMg { get; set; }

    /// <summary>
    /// The estimated length of filament used in meters.
    /// </summary>
    public double? EstimatedLengthInM { get; set; }

    /// <summary>
    /// The actual length of filament used in meters.
    /// </summary>
    public double? LengthInM { get; set; }

    /// <summary>
    /// The estimated length of filament used in meters.
    /// </summary>
    public double? EstimatedVolumeMl { get; set; }

    /// <summary>
    /// The actual volume of filament used in milliliters
    /// </summary>
    public double? VolumeMl { get; set; }

    /// <summary>
    /// Determines if the user entered the length as the "source of truth", or weight. 
    /// True means the user entered the filament usage as a length, while false means the user entered usage as Mg.
    /// </summary>
    [Obsolete("Use EstimatedSource Instead")]
    public bool IsEstimatedLengthSource { get; set; }

    /// <summary>
    /// Determines if the user entered the length as the "source of truth", or weight. 
    /// True means the user entered the filament usage as a length, while false means the user entered usage as Mg.
    /// </summary>
    [Obsolete("Use Source Instead")]
    public bool IsActualLengthSource { get; set; }

    /// <summary>
    /// The source of truth for the estimated sections
    /// </summary>
    public SourceMeasurement? EstimatedSource { get; set; }

    /// <summary>
    /// The source of truth for the actual sections
    /// </summary>
    public SourceMeasurement? Source { get; set; }

    /// <summary>
    /// Any notes for this filament usage.
    /// </summary>
    public string? Notes { get; set; }
}
