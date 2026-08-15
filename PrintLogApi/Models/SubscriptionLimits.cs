#nullable enable

namespace PrintLogApi.Models
{
    /// <summary>
    /// Subscription tier limits shared across services.
    /// </summary>
    public static class SubscriptionLimits
    {
        public const int FreeMaxImagesPerPrint = 5;
        public const int ProMaxImagesPerPrint = 20;

        public const int FreeMaxFilesPerPrint = 0;
        public const int ProMaxFilesPerPrint = 5;

        public const long FreeMaxFileStorageBytes = 0L;
        public const long ProMaxFileStorageBytes = 50L * 1024 * 1024 * 1024; // 50 GB
    }
}
