namespace PrintLogApi.Models.DTOs.Subscription;

public class SubscriptionDto
{
    public string? Status { get; set; }
    public string? Plan { get; set; }
    public DateTimeOffset? CurrentPeriodEnd { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    public bool IsPro { get; set; }
    /// <summary>
    /// Images allowed per print, per filament and per printer. One cap governs all three.
    /// </summary>
    public int MaxImages { get; set; }

    /// <summary>
    /// Kept for wire compatibility with clients that predate <see cref="MaxImages"/>.
    /// Always carries the same value.
    /// </summary>
    public int MaxImagesPerPrint { get; set; }
    public int MaxFilesPerPrint { get; set; }
    public long MaxFileStorageBytes { get; set; }
    public long UsedFileStorageBytes { get; set; }
}
