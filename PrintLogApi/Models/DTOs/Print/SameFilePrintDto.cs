using static PrintLogApi.Models.Print;

namespace PrintLogApi.Models.DTOs.Print;

/// <summary>One other print of the same file, enough to show and link it.</summary>
public class SameFilePrintDto
{
    public long Id { get; set; }

    public string? Title { get; set; }

    public PrintStatus Status { get; set; }

    public DateTimeOffset? StartDate { get; set; }

    public DateTimeOffset CreatedDate { get; set; }

    /// <summary>The print's default image, or null when it has none.</summary>
    public int? DefaultPrintImageId { get; set; }
}
