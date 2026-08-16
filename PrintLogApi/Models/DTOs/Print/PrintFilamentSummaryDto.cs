using PrintLogApi.Models.DTOs.Filament;
using static PrintLogApi.Models.PrintFilament;

namespace PrintLogApi.Models.DTOs.Print;

public class PrintFilamentSummaryDto
{
    public Guid Id { get; set; }

    public FilamentSummaryDto? Filament { get; set; }

    public int? EstimatedAmountMg { get; set; }
    public int? AmountMg { get; set; }

    /// <summary>
    /// The estimated length of filament used in meters.
    /// </summary>
    /// 
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

    public string? Notes { get; set; }
}
