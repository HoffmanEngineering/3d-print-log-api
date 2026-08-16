using static PrintLogApi.Models.Print;

namespace PrintLogApi.Models.DTOs.Print;

public class PrintDetailReport
{
    public DateTimeOffset? StartDate { get; set; }
    public string? Title { get; set; }

    public string? PrinterName { get; set; }

    public string? PrinterMake { get; set; }

    public string? PrinterModel { get; set; }

    public int? EstimatedPrintTimeInSeconds { get; set; }
    public double? EstimatedFilamentUsageG { get; set; }
    public int? PrintTimeInSeconds { get; set; }
    /// <summary>
    /// Filament usage in milligrams 
    /// </summary>
    public double? FilamentUsageG { get; set; }

    public string? FilamentType { get; set; }

    public string? Notes { get; set; }

    public string? Url { get; set; }


    public PrintStatus Status { get; set; }

    public PrintViewStatus ViewStatus { get; set; }
}
