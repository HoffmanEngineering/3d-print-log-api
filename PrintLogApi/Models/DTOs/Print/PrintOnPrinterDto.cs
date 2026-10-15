using static PrintLogApi.Models.Print;

namespace PrintLogApi.Models.DTOs.Print;

/// <summary>
/// A print already logged on a printer (<c>GET /api/Prints/on-printer</c>): what a connector
/// backfilling the printer's history needs to tell whether a job is logged already.
/// </summary>
public class PrintOnPrinterDto
{
    public long Id { get; set; }

    public string? Title { get; set; }

    public string? FileName { get; set; }

    public PrintStatus Status { get; set; }

    public DateTimeOffset? StartDate { get; set; }

    public DateTimeOffset CreatedDate { get; set; }

    /// <summary>The connector pair the print was logged under, if any (<c>moonraker</c>, <c>mcp</c>…).</summary>
    public string? ExternalSource { get; set; }

    public string? ExternalId { get; set; }
}
