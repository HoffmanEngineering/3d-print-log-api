using System;

namespace PrintLogApi.Models.DTOs.Subscription
{
    public class SubscriptionDto
    {
        public string Status { get; set; }
        public string Plan { get; set; }
        public DateTimeOffset? CurrentPeriodEnd { get; set; }
        public bool CancelAtPeriodEnd { get; set; }
        public bool IsPro { get; set; }
    }
}
