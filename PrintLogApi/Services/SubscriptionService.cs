using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Subscription;
using PrintLogApi.Models.Stripe;
using PrintLogApi.Services.Billing;
using Stripe;
using Stripe.Checkout;

namespace PrintLogApi.Services
{
    public class SubscriptionService : ISubscriptionService
    {
        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;
        private readonly TelemetryClient _telemetry;
        private readonly StripeOptions _stripeOptions;
        private readonly INotificationService _notificationService;
        private readonly IStripeGateway _stripe;

        public SubscriptionService(
            PrintLogContext context,
            IMapper mapper,
            TelemetryClient telemetry,
            IOptions<StripeOptions> stripeOptions,
            INotificationService notificationService,
            IStripeGateway stripe)
        {
            _context = context;
            _mapper = mapper;
            _telemetry = telemetry;
            _stripeOptions = stripeOptions.Value;
            _notificationService = notificationService;
            _stripe = stripe;
        }

        public async Task<SubscriptionDto> GetSubscriptionForUser(long userId)
        {
            var subscription = await _context.Subscriptions
                .Where(s => s.UserId == userId)
                .AsNoTracking()
                .SingleOrDefaultAsync();

            bool isPro = subscription?.Status == SubscriptionStatus.Active;

            SubscriptionDto dto;
            if (subscription == null)
            {
                dto = new SubscriptionDto
                {
                    Status = "none",
                    Plan = "free",
                    IsPro = false,
                    CancelAtPeriodEnd = false,
                    CurrentPeriodEnd = null,
                };
            }
            else
            {
                dto = _mapper.Map<SubscriptionDto>(subscription);
            }

            dto.MaxImagesPerPrint = isPro ? SubscriptionLimits.ProMaxImagesPerPrint : SubscriptionLimits.FreeMaxImagesPerPrint;
            dto.MaxFilesPerPrint = isPro ? SubscriptionLimits.ProMaxFilesPerPrint : SubscriptionLimits.FreeMaxFilesPerPrint;
            dto.MaxFileStorageBytes = isPro ? SubscriptionLimits.ProMaxFileStorageBytes : SubscriptionLimits.FreeMaxFileStorageBytes;
            dto.UsedFileStorageBytes = await _context.PrintAttachments
                .Where(pa => pa.CreatedById == userId)
                .SumAsync(pa => (long?)pa.File.Size) ?? 0L;

            return dto;
        }

        public async Task<string> CreateCheckoutSession(long userId, string planId, string successUrl, string cancelUrl)
        {
            var priceId = planId switch
            {
                "pro_monthly" => _stripeOptions.ProMonthlyPriceId,
                "pro_annual" => _stripeOptions.ProAnnualPriceId,
                _ => throw new SubscriptionException($"Invalid plan: {planId}")
            };

            var subscription = await _context.Subscriptions
                .Where(s => s.UserId == userId)
                .SingleOrDefaultAsync();

            string customerId = subscription?.StripeCustomerId;

            if (string.IsNullOrEmpty(customerId))
            {
                customerId = await _stripe.CreateCustomerAsync(userId, $"customer-{userId}");

                if (subscription == null)
                {
                    subscription = new Models.Subscription
                    {
                        UserId = userId,
                        StripeCustomerId = customerId,
                        Status = SubscriptionStatus.None,
                        Plan = SubscriptionPlan.Free,
                        CreatedById = userId,
                        UpdatedById = userId
                    };
                    _context.Subscriptions.Add(subscription);
                }
                else
                {
                    subscription.StripeCustomerId = customerId;
                    subscription.UpdatedById = userId;
                }

                await _context.SaveChangesAsync();
            }

            var sessionResult = await _stripe.CreateCheckoutSessionAsync(
                new StripeCheckoutSessionRequest
                {
                    UserId = userId,
                    CustomerId = customerId,
                    PriceId = priceId,
                    SuccessUrl = successUrl,
                    CancelUrl = cancelUrl
                },
                Guid.NewGuid().ToString());

            _telemetry.TrackEvent("Subscription_CheckoutSessionCreated", new Dictionary<string, string>
            {
                { "userId", userId.ToString() },
                { "planId", planId }
            });

            return sessionResult.Url;
        }

        public async Task<string> CreateCustomerPortalSession(long userId, string returnUrl)
        {
            var subscription = await _context.Subscriptions
                .Where(s => s.UserId == userId)
                .AsNoTracking()
                .SingleOrDefaultAsync();

            if (subscription == null || string.IsNullOrEmpty(subscription.StripeCustomerId))
                throw new SubscriptionException("No Stripe customer found for this user.");

            return await _stripe.CreateBillingPortalSessionAsync(subscription.StripeCustomerId, returnUrl);
        }

        public async Task HandleStripeWebhook(string json, string signature)
        {
            var stripeEvent = _stripe.ConstructWebhookEvent(json, signature);

            switch (stripeEvent.Type)
            {
                case EventTypes.CheckoutSessionCompleted:
                    await HandleCheckoutSessionCompleted(stripeEvent);
                    break;
                case EventTypes.CustomerSubscriptionUpdated:
                    await HandleSubscriptionUpdated(stripeEvent);
                    break;
                case EventTypes.CustomerSubscriptionDeleted:
                    await HandleSubscriptionDeleted(stripeEvent);
                    break;
                case EventTypes.InvoicePaymentFailed:
                    await HandlePaymentFailed(stripeEvent);
                    break;
            }
        }

        public async Task CancelSubscriptionAtPeriodEnd(long userId)
        {
            var subscription = await _context.Subscriptions
                .Where(s => s.UserId == userId && s.Status == SubscriptionStatus.Active)
                .SingleOrDefaultAsync();

            if (subscription == null || string.IsNullOrEmpty(subscription.StripeSubscriptionId))
                return;

            await _stripe.SetSubscriptionCancelAtPeriodEndAsync(subscription.StripeSubscriptionId, true);

            subscription.CancelAtPeriodEnd = true;
            await _context.SaveChangesAsync();

            _telemetry.TrackEvent("Subscription_MarkedForCancellation", new Dictionary<string, string>
            {
                { "userId", userId.ToString() }
            });
        }

        public async Task CancelSubscriptionImmediately(long userId)
        {
            var subscription = await _context.Subscriptions
                .Where(s => s.UserId == userId && s.Status == SubscriptionStatus.Active)
                .SingleOrDefaultAsync();

            if (subscription == null || string.IsNullOrEmpty(subscription.StripeSubscriptionId))
                return;

            await _stripe.CancelSubscriptionAsync(subscription.StripeSubscriptionId);

            subscription.Status = SubscriptionStatus.Canceled;
            subscription.CanceledAt = DateTimeOffset.UtcNow;
            await _context.SaveChangesAsync();

            _telemetry.TrackEvent("Subscription_CanceledImmediately", new Dictionary<string, string>
            {
                { "userId", userId.ToString() }
            });
        }

        public async Task ResumeSubscription(long userId)
        {
            var subscription = await _context.Subscriptions
                .Where(s => s.UserId == userId && s.Status == SubscriptionStatus.Active && s.CancelAtPeriodEnd)
                .SingleOrDefaultAsync();

            if (subscription == null || string.IsNullOrEmpty(subscription.StripeSubscriptionId))
                return;

            await _stripe.SetSubscriptionCancelAtPeriodEndAsync(subscription.StripeSubscriptionId, false);

            subscription.CancelAtPeriodEnd = false;
            subscription.CanceledAt = null;
            await _context.SaveChangesAsync();

            _telemetry.TrackEvent("Subscription_Resumed", new Dictionary<string, string>
            {
                { "userId", userId.ToString() }
            });
        }

        private async Task HandleCheckoutSessionCompleted(Event stripeEvent)
        {
            var session = stripeEvent.Data.Object as Session;
            if (session == null) return;

            var stripeSubscriptionId = session.SubscriptionId;
            var customerId = session.CustomerId;

            var stripeSubscription = await _stripe.GetSubscriptionAsync(stripeSubscriptionId);

            var subscription = await _context.Subscriptions
                .Where(s => s.StripeCustomerId == customerId)
                .SingleOrDefaultAsync();

            if (subscription == null)
            {
                if (session.Metadata.TryGetValue("userId", out var userIdStr) && long.TryParse(userIdStr, out var userId))
                {
                    subscription = await _context.Subscriptions
                        .Where(s => s.UserId == userId)
                        .SingleOrDefaultAsync();

                    if (subscription == null)
                    {
                        subscription = new Models.Subscription
                        {
                            UserId = userId,
                            StripeCustomerId = customerId,
                            CreatedById = userId,
                            UpdatedById = userId
                        };
                        _context.Subscriptions.Add(subscription);
                    }
                }
                else
                {
                    return;
                }
            }

            var priceId = stripeSubscription.PriceId;

            subscription.StripeSubscriptionId = stripeSubscriptionId;
            subscription.StripePriceId = priceId;
            subscription.Status = SubscriptionStatus.Active;
            subscription.Plan = MapPriceIdToPlan(priceId);
            subscription.CurrentPeriodStart = stripeSubscription.CurrentPeriodStart;
            subscription.CurrentPeriodEnd = stripeSubscription.CurrentPeriodEnd;
            subscription.CancelAtPeriodEnd = stripeSubscription.CancelAtPeriodEnd;

            await _context.SaveChangesAsync();

            _telemetry.TrackEvent("Subscription_Activated", new Dictionary<string, string>
            {
                { "userId", subscription.UserId.ToString() },
                { "plan", subscription.Plan.ToString() }
            });

            try
            {
                var planDisplay = subscription.Plan switch
                {
                    SubscriptionPlan.ProMonthly => "Pro Monthly",
                    SubscriptionPlan.ProAnnual => "Pro Annual",
                    _ => "Pro"
                };
                await _notificationService.CreateSubscriptionActivatedNotification(subscription.UserId, planDisplay);
            }
            catch (Exception ex)
            {
                _telemetry.TrackException(ex);
            }
        }

        private async Task HandleSubscriptionUpdated(Event stripeEvent)
        {
            var stripeSubscription = stripeEvent.Data.Object as global::Stripe.Subscription;
            if (stripeSubscription == null) return;

            var subscription = await _context.Subscriptions
                .Where(s => s.StripeSubscriptionId == stripeSubscription.Id)
                .SingleOrDefaultAsync();

            if (subscription == null) return;

            var stripeItem = stripeSubscription.Items.Data.FirstOrDefault();
            var priceId = stripeItem?.Price.Id;

            subscription.StripePriceId = priceId;
            subscription.Status = MapStripeStatus(stripeSubscription.Status);
            subscription.Plan = MapPriceIdToPlan(priceId);
            subscription.CurrentPeriodStart = stripeItem?.CurrentPeriodStart;
            subscription.CurrentPeriodEnd = stripeItem?.CurrentPeriodEnd;
            subscription.CancelAtPeriodEnd = stripeSubscription.CancelAtPeriodEnd;

            if (stripeSubscription.CanceledAt.HasValue)
                subscription.CanceledAt = stripeSubscription.CanceledAt;

            await _context.SaveChangesAsync();

            _telemetry.TrackEvent("Subscription_Updated", new Dictionary<string, string>
            {
                { "userId", subscription.UserId.ToString() },
                { "status", subscription.Status.ToString() },
                { "plan", subscription.Plan.ToString() }
            });
        }

        private async Task HandleSubscriptionDeleted(Event stripeEvent)
        {
            var stripeSubscription = stripeEvent.Data.Object as global::Stripe.Subscription;
            if (stripeSubscription == null) return;

            var subscription = await _context.Subscriptions
                .Where(s => s.StripeSubscriptionId == stripeSubscription.Id)
                .SingleOrDefaultAsync();

            if (subscription == null) return;

            subscription.Status = SubscriptionStatus.Canceled;
            subscription.Plan = SubscriptionPlan.Free;
            subscription.CanceledAt = DateTimeOffset.UtcNow;

            await _context.SaveChangesAsync();

            try
            {
                await _notificationService.CreateSubscriptionCanceledNotification(subscription.UserId);
            }
            catch (Exception ex)
            {
                _telemetry.TrackException(ex);
            }

            _telemetry.TrackEvent("Subscription_Canceled", new Dictionary<string, string>
            {
                { "userId", subscription.UserId.ToString() }
            });
        }

        private async Task HandlePaymentFailed(Event stripeEvent)
        {
            var invoice = stripeEvent.Data.Object as Invoice;
            if (invoice == null) return;

            var subscription = await _context.Subscriptions
                .Where(s => s.StripeCustomerId == invoice.CustomerId)
                .SingleOrDefaultAsync();

            if (subscription == null) return;

            subscription.Status = SubscriptionStatus.PastDue;

            await _context.SaveChangesAsync();

            try
            {
                await _notificationService.CreateSubscriptionPaymentFailedNotification(subscription.UserId);
            }
            catch (Exception ex)
            {
                _telemetry.TrackException(ex);
            }

            _telemetry.TrackEvent("Subscription_PaymentFailed", new Dictionary<string, string>
            {
                { "userId", subscription.UserId.ToString() }
            });
        }

        private SubscriptionPlan MapPriceIdToPlan(string priceId)
        {
            if (priceId == _stripeOptions.ProMonthlyPriceId) return SubscriptionPlan.ProMonthly;
            if (priceId == _stripeOptions.ProAnnualPriceId) return SubscriptionPlan.ProAnnual;
            return SubscriptionPlan.Free;
        }

        private static SubscriptionStatus MapStripeStatus(string stripeStatus)
        {
            return stripeStatus switch
            {
                "active" => SubscriptionStatus.Active,
                "past_due" => SubscriptionStatus.PastDue,
                "canceled" => SubscriptionStatus.Canceled,
                "unpaid" => SubscriptionStatus.PastDue,
                "trialing" => SubscriptionStatus.Active,
                _ => SubscriptionStatus.None
            };
        }
    }
}
