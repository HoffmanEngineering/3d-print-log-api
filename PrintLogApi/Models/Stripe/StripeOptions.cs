#nullable enable

namespace PrintLogApi.Models.Stripe
{
    public class StripeOptions
    {
        public string? SecretKey { get; set; }
        public string? WebhookSecret { get; set; }
        public string? ProMonthlyPriceId { get; set; }
        public string? ProAnnualPriceId { get; set; }
    }
}
