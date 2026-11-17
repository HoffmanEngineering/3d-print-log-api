using static PrintLogApi.Models.Print;

namespace PrintLogApi.Models.DTOs.Print;

/// <summary>
/// A print a connector may claim (#165): what it needs to match the print to the job it sees
/// starting, and to decide what it still has to send.
/// </summary>
public class ClaimablePrintDto
{
    public long Id { get; set; }

    public string? Title { get; set; }

    public long PrinterId { get; set; }

    public PrintStatus Status { get; set; }

    /// <summary>How the print reached the API: <c>SlicerPlugin</c>, <c>Web</c>… Kept by a claim.</summary>
    public PrintSource Source { get; set; }

    public string? FileName { get; set; }

    /// <summary>
    /// The file's identity as lowercase hex, when the creator or a claim sent one: the SHA-1 of the
    /// G-code, or a connector's own identity for the file (see <c>PrintFileHashes</c>).
    /// </summary>
    public string? FileHash { get; set; }

    public DateTimeOffset CreatedDate { get; set; }

    public DateTimeOffset? StartDate { get; set; }

    /// <summary>
    /// Whether slicer settings are already recorded for the print (its slicer is known), so a
    /// connector can skip sending its own.
    /// </summary>
    public bool HasSlicerSettings { get; set; }

    /// <summary>The usage rows, with the slot each was fed from where known.</summary>
    public ICollection<PrintFilamentSummaryDto>? FilamentUsage { get; set; }
}
