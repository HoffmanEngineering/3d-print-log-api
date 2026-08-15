#nullable enable

using System;

namespace PrintLogApi.Models.DTOs.Subscription
{
    public class SubscriptionDto
    {
        public string? Status { get; set; }
        public string? Plan { get; set; }
        public DateTimeOffset? CurrentPeriodEnd { get; set; }
        public bool CancelAtPeriodEnd { get; set; }
        public bool IsPro { get; set; }
        public int MaxImagesPerPrint { get; set; }
        public int MaxFilesPerPrint { get; set; }
        public long MaxFileStorageBytes { get; set; }
        public long UsedFileStorageBytes { get; set; }
    }
}
