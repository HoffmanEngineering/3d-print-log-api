using System.Threading.Tasks;
using PrintLogApi.Models.DTOs.Subscription;

namespace PrintLogApi.Services
{
    public interface ISubscriptionService
    {
        Task<SubscriptionDto> GetSubscriptionForUser(long userId);
        Task<string> CreateCheckoutSession(long userId, string planId, string successUrl, string cancelUrl);
        Task<string> CreateCustomerPortalSession(long userId, string returnUrl);
        Task HandleStripeWebhook(string json, string signature);

        /// <summary>
        /// Cancels the user's active subscription at the end of the current billing period.
        /// The user retains Pro access until the current period ends.
        /// Does nothing if the user has no active subscription.
        /// </summary>
        Task CancelSubscriptionAtPeriodEnd(long userId);

        /// <summary>
        /// Cancels the user's active subscription immediately with no proration.
        /// Does nothing if the user has no active subscription.
        /// </summary>
        Task CancelSubscriptionImmediately(long userId);

        /// <summary>
        /// Un-cancels a subscription that was set to cancel at period end.
        /// Does nothing if the subscription is not active or not flagged for cancellation.
        /// </summary>
        Task ResumeSubscription(long userId);
    }
}
