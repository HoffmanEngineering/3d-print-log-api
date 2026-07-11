using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using PrintLogApi.Models.Stripe;
using Stripe;
using Stripe.Checkout;

namespace PrintLogApi.Services.Billing
{
    public class StripeGateway : IStripeGateway
    {
        private readonly StripeOptions _stripeOptions;

        public StripeGateway(IOptions<StripeOptions> stripeOptions)
        {
            _stripeOptions = stripeOptions.Value;
        }

        public async Task<string> CreateCustomerAsync(long userId, string idempotencyKey)
        {
            var customerService = new CustomerService();
            var customer = await customerService.CreateAsync(
                new CustomerCreateOptions
                {
                    Metadata = new Dictionary<string, string> { { "userId", userId.ToString() } }
                },
                new RequestOptions { IdempotencyKey = idempotencyKey });
            return customer.Id;
        }

        public async Task<StripeCheckoutSessionResult> CreateCheckoutSessionAsync(StripeCheckoutSessionRequest request, string idempotencyKey)
        {
            var sessionService = new SessionService();
            var session = await sessionService.CreateAsync(
                new SessionCreateOptions
                {
                    Customer = request.CustomerId,
                    PaymentMethodTypes = new List<string> { "card" },
                    LineItems = new List<SessionLineItemOptions>
                    {
                        new SessionLineItemOptions { Price = request.PriceId, Quantity = 1 }
                    },
                    Mode = "subscription",
                    SuccessUrl = request.SuccessUrl,
                    CancelUrl = request.CancelUrl,
                    Metadata = new Dictionary<string, string> { { "userId", request.UserId.ToString() } },
                    SubscriptionData = new SessionSubscriptionDataOptions
                    {
                        Metadata = new Dictionary<string, string> { { "userId", request.UserId.ToString() } }
                    }
                },
                new RequestOptions { IdempotencyKey = idempotencyKey });

            return new StripeCheckoutSessionResult
            {
                Id = session.Id,
                Url = session.Url,
                ExpiresAt = session.ExpiresAt
            };
        }

        public async Task<StripeSubscriptionInfo> GetSubscriptionAsync(string subscriptionId)
        {
            var subscriptionService = new global::Stripe.SubscriptionService();
            var sub = await subscriptionService.GetAsync(subscriptionId);
            return Map(sub);
        }

        public async Task<IReadOnlyList<StripeSubscriptionInfo>> ListSubscriptionsAsync(string customerId)
        {
            var subscriptionService = new global::Stripe.SubscriptionService();
            var options = new SubscriptionListOptions
            {
                Customer = customerId,
                Status = "all",
                Limit = 100
            };

            // Auto-page so a customer with many historical subscriptions cannot hide a live
            // subscription beyond the first page.
            var results = new List<StripeSubscriptionInfo>();
            await foreach (var sub in subscriptionService.ListAutoPagingAsync(options))
            {
                results.Add(Map(sub));
            }
            return results;
        }

        public async Task SetSubscriptionCancelAtPeriodEndAsync(string subscriptionId, bool cancelAtPeriodEnd)
        {
            var subscriptionService = new global::Stripe.SubscriptionService();
            await subscriptionService.UpdateAsync(subscriptionId, new SubscriptionUpdateOptions
            {
                CancelAtPeriodEnd = cancelAtPeriodEnd
            });
        }

        public async Task CancelSubscriptionAsync(string subscriptionId)
        {
            var subscriptionService = new global::Stripe.SubscriptionService();
            await subscriptionService.CancelAsync(subscriptionId);
        }

        public async Task<string> CreateBillingPortalSessionAsync(string customerId, string returnUrl)
        {
            var sessionService = new global::Stripe.BillingPortal.SessionService();
            var session = await sessionService.CreateAsync(new global::Stripe.BillingPortal.SessionCreateOptions
            {
                Customer = customerId,
                ReturnUrl = returnUrl
            });
            return session.Url;
        }

        public global::Stripe.Event ConstructWebhookEvent(string json, string signature)
        {
            return EventUtility.ConstructEvent(json, signature, _stripeOptions.WebhookSecret);
        }

        private static StripeSubscriptionInfo Map(global::Stripe.Subscription sub)
        {
            var item = sub.Items?.Data?.FirstOrDefault();
            return new StripeSubscriptionInfo
            {
                Id = sub.Id,
                Status = sub.Status,
                PriceId = item?.Price?.Id,
                CurrentPeriodStart = AsUtc(item?.CurrentPeriodStart),
                CurrentPeriodEnd = AsUtc(item?.CurrentPeriodEnd),
                CancelAtPeriodEnd = sub.CancelAtPeriodEnd,
                CanceledAt = AsUtc(sub.CanceledAt)
            };
        }

        // Stripe.NET returns UTC timestamps, but be explicit so a downstream
        // DateTime->DateTimeOffset conversion never reinterprets an Unspecified Kind as local.
        private static DateTime? AsUtc(DateTime? value)
        {
            return value.HasValue
                ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
                : (DateTime?)null;
        }
    }
}
