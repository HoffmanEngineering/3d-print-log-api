using System;

namespace PrintLogApi.Models.DTOs.Subscription
{
    public class SubscriptionDto
    {
        public SubscriptionStatus Status { get; set; }
        public SubscriptionPlan Plan { get; set; }
        public DateTimeOffset? CurrentPeriodEnd { get; set; }
        public bool CancelAtPeriodEnd { get; set; }
        public bool IsPro { get; set; }
    }
}
