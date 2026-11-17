using PrintLogApi.Models.DTOs.Printer;
using PrintLogApi.Services;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Models.DTOs.Print;

public class PrintSummaryDTO
{

    public long Id { get; set; }

    public string? Title { get; set; }

    public PrinterSummary? Printer { get; set; }

    public DateTimeOffset? StartDate { get; set; }

    public PrintStatus Status { get; set; }

    public int? EstimatedPrintTimeInSeconds { get; set; }
    public int? PrintTimeInSeconds { get; set; }

    public ICollection<PrintFilamentSummaryDto>? FilamentUsage { get; set; }

    public int SumActualFilamentWeightMg { get; set; }

    public int SumEstimatedFilamentWeightMg { get; set; }

    public int TotalFilamentWeightMg { get; set; }

    /// <summary>
    /// Resin used, in milliliters (#294): only usage recorded as a volume, summed by the same
    /// actual-else-estimate rule as the gram total. Beside TotalFilamentWeightMg, never added to it.
    /// </summary>
    public double TotalFilamentVolumeMl { get; set; }


    public long CreatedByUserId { get; set; }

    public int DefaultPrintImageId { get; set; }

    /// <summary>
    /// Path of the default image's thumbnail, relative to the API base URL. Null when the print
    /// has no default image (<see cref="DefaultPrintImageId"/> is 0).
    /// </summary>
    public string? DefaultPrintImageThumbnailUrl => ImageUrls.PrintThumbnailOrNull(Id, DefaultPrintImageId);

    public int CommentCount { get; set; }

    public Guid? ProjectId { get; set; }
    public string? ProjectName { get; set; }

    /// <summary>The connector that logged this print, if any. Shown to the creator only.</summary>
    public string? ExternalSource { get; set; }

    /// <summary>The job's id in <see cref="ExternalSource"/>. Shown to the creator only.</summary>
    public string? ExternalId { get; set; }
}
