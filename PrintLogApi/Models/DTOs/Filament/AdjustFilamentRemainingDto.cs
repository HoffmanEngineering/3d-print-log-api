namespace PrintLogApi.Models.DTOs.Filament;

/// <summary>
/// A correction to how much of a spool remains, for integrations (#236).
/// </summary>
public class AdjustFilamentRemainingDto
{
    /// <summary>The unit of <c>delta</c>: <c>weight</c> (grams), <c>length</c> (millimeters) or <c>volume</c> (milliliters). Case-insensitive.</summary>
    public string? Source { get; set; }

    /// <summary>Signed amount in the unit of <c>source</c>. Positive adds, negative removes. Must be finite, not 0, and at most 2,000,000 in magnitude.</summary>
    public double Delta { get; set; }

    /// <summary>Optional note, at most 1000 characters (checked in the action so the 400 carries a `code`).</summary>
    public string? Notes { get; set; }
}

/// <summary>The spool's remaining weight around an adjustment, always in grams.</summary>
public class AdjustFilamentRemainingResultDto
{
    public double BeforeGrams { get; set; }

    public double AfterGrams { get; set; }
}
