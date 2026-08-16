using PrintLogApi.Models.DTOs.Printer;
using PrintLogApi.Models.DTOs.User;
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

    public int CommentCount { get; set; }
}
