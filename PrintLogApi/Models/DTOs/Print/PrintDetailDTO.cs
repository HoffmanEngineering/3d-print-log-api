using System.ComponentModel.DataAnnotations;
using PrintLogApi.Models.DTOs.Comments;
using PrintLogApi.Models.DTOs.Printer;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Models.DTOs.Print;

public class PrintDetailDTO
{
    public long Id { get; set; }

    public string? Title { get; set; }

    public DateTimeOffset? StartDate { get; set; }

    public long PrinterId { get; set; }

    public PrinterSummary? Printer { get; set; }

    public int? EstimatedPrintTimeInSeconds { get; set; }
    public int? EstimatedFilamentUsageMg { get; set; }
    public int? PrintTimeInSeconds { get; set; }
    /// <summary>
    /// Filament usage in milligrams 
    /// </summary>
    public int? FilamentUsageMg { get; set; }

    public string? FilamentType { get; set; }

    public ICollection<PrintFilamentSummaryDto>? FilamentUsage { get; set; }

    [StringLength(50000)]
    public string? Notes { get; set; }

    public string? Url { get; set; }

    [MaxLength(1000)]
    public string? FileName { get; set; }

    public long CreatedByUserId { get; set; }

    public bool AllowComments { get; set; }

    public bool AllowFileDownloads { get; set; }

    public PrintStatus Status { get; set; }

    public PrintViewStatus ViewStatus { get; set; }

    public Guid? ProjectId { get; set; }

    public ICollection<PrintImageDto>? Images { get; set; }

    public ICollection<CommentDetailDto>? Comments { get; set; }

    /// <summary>The connector that logged this print, if any. Read-only; shown to the creator only.</summary>
    public string? ExternalSource { get; set; }

    /// <summary>The job's id in <see cref="ExternalSource"/>. Read-only; shown to the creator only.</summary>
    public string? ExternalId { get; set; }

    /// <summary>
    /// The display name of the connection that logged this print, for "Logged automatically by …".
    /// Null when no connection logged it or it has been deleted. Shown to the creator only.
    /// </summary>
    public string? ConnectionDisplayName { get; set; }
}
