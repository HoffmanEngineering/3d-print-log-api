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
    }
}
