namespace PrintLogApi.Models.DTOs.Printer;

/// <summary>
/// One entry in the caller's printer-thumbnail map.
/// </summary>
public class PrinterThumbnailDto
{
    public long PrinterId { get; set; }

    /// <summary>Signed thumbnail URL, or null when signing failed.</summary>
    public string? ThumbnailUrl { get; set; }
}
