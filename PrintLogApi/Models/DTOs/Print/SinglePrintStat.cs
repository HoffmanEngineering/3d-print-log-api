namespace PrintLogApi.Models.DTOs.Print;

/// <summary>
/// Used to encapsulate a single statistic, be it total print time, filament usage, etc.
/// </summary>
public class SinglePrintStat
{
    public string? Stat { get; set; }
}
