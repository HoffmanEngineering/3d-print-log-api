using PrintLogApi.Models.DTOs.Printer;
using PrintLogApi.Models.DTOs.User;
using PrintLogApi.Services;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Models.DTOs.Print;

public class PrintFeedSummaryDto
{

    public long Id { get; set; }

    public string? Title { get; set; }

    public UserSummaryDto? CreatedBy { get; set; }

    public PrinterSummary? Printer { get; set; }

    public DateTimeOffset? StartDate { get; set; }

    public DateTimeOffset CreatedDate { get; set; }

    public PrintStatus Status { get; set; }

    public int? EstimatedPrintTimeInSeconds { get; set; }
    public int? PrintTimeInSeconds { get; set; }

    public int DefaultPrintImageId { get; set; }

    /// <summary>
    /// Path of the default image's thumbnail, relative to the API base URL. Null when the print
    /// has no default image (<see cref="DefaultPrintImageId"/> is 0).
    /// </summary>
    public string? DefaultPrintImageThumbnailUrl => ImageUrls.PrintThumbnailOrNull(Id, DefaultPrintImageId);

    public int CommentCount { get; set; }
}
