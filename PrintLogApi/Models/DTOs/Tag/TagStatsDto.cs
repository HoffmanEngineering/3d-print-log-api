namespace PrintLogApi.Models.DTOs.Tag;

/// <summary>A tag with totals over every print that carries it.</summary>
public class TagStatsDto
{
    public Guid Id { get; set; }

    public string? Name { get; set; }

    public string? Color { get; set; }

    public int PrintCount { get; set; }

    /// <summary>
    /// Measured print time where recorded, else the slicer's estimate, summed. The same rule as
    /// a project's total.
    /// </summary>
    public long TotalPrintTimeInSeconds { get; set; }

    /// <summary>Actual filament weight where recorded, else the estimate, summed.</summary>
    public long TotalFilamentWeightMg { get; set; }

    /// <summary>
    /// Filament plus electricity cost over the prints that could be costed, in
    /// <see cref="TagStatsResponse.Currency"/>. Null when none could be, never a made-up 0.
    /// </summary>
    public decimal? TotalCost { get; set; }

    /// <summary>How many of <see cref="PrintCount"/> contributed to <see cref="TotalCost"/>.</summary>
    public int CostedPrintCount { get; set; }
}
