using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PrintLogApi.Services.Billing;

namespace PrintLogApi.IntegrationTests.Services
{
    public class FakeStripeGateway : IStripeGateway
    {
        public int CreateCustomerCallCount;
        public int CreateCheckoutSessionCallCount;
        public string LastCheckoutIdempotencyKey;

        // Keyed by customerId. Populate to simulate existing Stripe subscriptions (reconciliation).
        public Dictionary<string, List<StripeSubscriptionInfo>> SubscriptionsByCustomer { get; } = new();

        // Returned by GetSubscriptionAsync, keyed by subscription id.
        public Dictionary<string, StripeSubscriptionInfo> SubscriptionsById { get; } = new();

        public Func<string> NextCustomerId = () => $"cus_{Guid.NewGuid():N}";
        public Func<string> NextSessionId = () => $"cs_{Guid.NewGuid():N}";
        public Func<string> NextSessionUrl = () => $"https://checkout.stripe.test/{Guid.NewGuid():N}";
        public DateTime NextSessionExpiresAt = DateTime.UtcNow.AddHours(24);

        public global::Stripe.Event QueuedWebhookEvent { get; set; }

        // Set to make the next CreateCheckoutSessionAsync throw (failure-boundary test).
        public Exception CheckoutFailure { get; set; }

        public Task<string> CreateCustomerAsync(long userId, string idempotencyKey)
        {
            Interlocked.Increment(ref CreateCustomerCallCount);
            return Task.FromResult(NextCustomerId());
        }

        public Task<StripeCheckoutSessionResult> CreateCheckoutSessionAsync(StripeCheckoutSessionRequest request, string idempotencyKey)
        {
            Interlocked.Increment(ref CreateCheckoutSessionCallCount);
            LastCheckoutIdempotencyKey = idempotencyKey;
            if (CheckoutFailure != null)
            {
                var ex = CheckoutFailure;
                CheckoutFailure = null; // fail once, then allow retry to succeed
                throw ex;
            }
            return Task.FromResult(new StripeCheckoutSessionResult
            {
                Id = NextSessionId(),
                Url = NextSessionUrl(),
                ExpiresAt = NextSessionExpiresAt
            });
        }

        public Task<StripeSubscriptionInfo> GetSubscriptionAsync(string subscriptionId)
        {
            SubscriptionsById.TryGetValue(subscriptionId, out var info);
            return Task.FromResult(info ?? new StripeSubscriptionInfo
            {
                Id = subscriptionId,
                Status = "active"
            });
        }

        public Task<IReadOnlyList<StripeSubscriptionInfo>> ListSubscriptionsAsync(string customerId)
        {
            SubscriptionsByCustomer.TryGetValue(customerId, out var list);
            return Task.FromResult((IReadOnlyList<StripeSubscriptionInfo>)(list ?? new List<StripeSubscriptionInfo>()));
        }

        public Task SetSubscriptionCancelAtPeriodEndAsync(string subscriptionId, bool cancelAtPeriodEnd) => Task.CompletedTask;
        public Task CancelSubscriptionAsync(string subscriptionId) => Task.CompletedTask;
        public Task<string> CreateBillingPortalSessionAsync(string customerId, string returnUrl) => Task.FromResult("https://portal.stripe.test/session");

        public global::Stripe.Event ConstructWebhookEvent(string json, string signature) => QueuedWebhookEvent;
    }
}
