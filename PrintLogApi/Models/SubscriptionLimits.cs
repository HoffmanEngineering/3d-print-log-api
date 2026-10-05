namespace PrintLogApi.Models;

/// <summary>
/// Subscription tier limits shared across services.
/// </summary>
public static class SubscriptionLimits
{
    /// <summary>
    /// Images per print, per filament and per printer share one pair of limits. They diverged
    /// historically (filament shipped at 3/10); unified upward so no user's images are stranded.
    /// </summary>
    public const int FreeMaxImages = 5;
    public const int ProMaxImages = 20;

    public const int FreeMaxFilesPerPrint = 0;
    public const int ProMaxFilesPerPrint = 5;

    /// <summary>
    /// File-attachment byte allowance. Applies to attachments ONLY - image uploads are measured
    /// against <see cref="ProMaxFileStorageBytes"/> regardless of tier.
    /// </summary>
    public const long FreeMaxFileStorageBytes = 0L;
    public const long ProMaxFileStorageBytes = 50L * 1024 * 1024 * 1024; // 50 GB
}
