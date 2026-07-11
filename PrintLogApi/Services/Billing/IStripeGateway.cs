using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PrintLogApi.Services.Billing
{
    public interface IStripeGateway
    {
        Task<string> CreateCustomerAsync(long userId, string idempotencyKey);
        Task<StripeCheckoutSessionResult> CreateCheckoutSessionAsync(StripeCheckoutSessionRequest request, string idempotencyKey);
        Task<StripeSubscriptionInfo> GetSubscriptionAsync(string subscriptionId);
        Task<IReadOnlyList<StripeSubscriptionInfo>> ListSubscriptionsAsync(string customerId);
        Task SetSubscriptionCancelAtPeriodEndAsync(string subscriptionId, bool cancelAtPeriodEnd);
        Task CancelSubscriptionAsync(string subscriptionId);
        Task<string> CreateBillingPortalSessionAsync(string customerId, string returnUrl);
        global::Stripe.Event ConstructWebhookEvent(string json, string signature);
    }

    public class StripeCheckoutSessionRequest
    {
        public long UserId { get; set; }
        public string CustomerId { get; set; }
        public string PriceId { get; set; }
        public string SuccessUrl { get; set; }
        public string CancelUrl { get; set; }
    }

    public class StripeCheckoutSessionResult
    {
        public string Id { get; set; }
        public string Url { get; set; }
        public DateTime ExpiresAt { get; set; }
    }

    public class StripeSubscriptionInfo
    {
        public string Id { get; set; }
        public string Status { get; set; }
        public string PriceId { get; set; }
        public DateTime? CurrentPeriodStart { get; set; }
        public DateTime? CurrentPeriodEnd { get; set; }
        public bool CancelAtPeriodEnd { get; set; }
        public DateTime? CanceledAt { get; set; }
    }
}
