using PrintLogApi.Models.DTOs.PrinterCategory;

namespace PrintLogApi.Models.DTOs.Printer;

/// <summary>
/// Simplified printer summary for list displays.
/// Uses lightweight filament DTOs to avoid expensive query calculations.
/// </summary>
public class PrinterSummarySimpleDto
{
    public long Id { get; set; }

    public string? Name { get; set; }

    public string? Make { get; set; }

    public string? Model { get; set; }

    public bool IsActive { get; set; }

    public double? WattageW { get; set; }

    /// <summary>How many filament positions the printer has. 1 for a single-tool printer.</summary>
    public int SlotCount { get; set; }

    public PrinterCategoryDto? Category { get; set; }

    public ICollection<PrinterFilamentForSummaryDto>? LoadedFilaments { get; set; }

    /// <summary>
    /// Signed thumbnail URL for this printer's default photo. Populated per-request, AFTER
    /// the cache read - a signed URL cached for the entry's lifetime would outlive its
    /// signature.
    /// </summary>
    public string? DefaultImageThumbnailUrl { get; set; }
}
