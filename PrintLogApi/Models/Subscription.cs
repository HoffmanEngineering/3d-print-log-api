using System;
using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models
{
    public class Subscription : TimestampEntity
    {
        [Key]
        public long Id { get; set; }

        [Required]
        public long UserId { get; set; }
        public User User { get; set; } = null!;

        [StringLength(255)]
        public string? StripeCustomerId { get; set; }

        [StringLength(255)]
        public string? StripeSubscriptionId { get; set; }

        [StringLength(255)]
        public string? StripePriceId { get; set; }

        public SubscriptionStatus Status { get; set; }
        public SubscriptionPlan Plan { get; set; }

        public DateTimeOffset? CurrentPeriodStart { get; set; }
        public DateTimeOffset? CurrentPeriodEnd { get; set; }
        public bool CancelAtPeriodEnd { get; set; }
        public DateTimeOffset? CanceledAt { get; set; }
    }
}
