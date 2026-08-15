#nullable enable

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PrintLogApi.Exceptions;
using PrintLogApi.Extensions;
using PrintLogApi.Models.DTOs.Subscription;
using PrintLogApi.Services;

namespace PrintLogApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class SubscriptionController : ControllerBase
    {
        private readonly ISubscriptionService _subscriptionService;
        private readonly TelemetryClient _telemetry;

        public SubscriptionController(
            ISubscriptionService subscriptionService,
            TelemetryClient telemetry)
        {
            _subscriptionService = subscriptionService;
            _telemetry = telemetry;
        }

        /// <summary>
        /// Get the current user's subscription status.
        /// </summary>
        [HttpGet("me")]
        public async Task<ActionResult<SubscriptionDto>> GetCurrentUserSubscription()
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
                return Unauthorized();

            var subscription = await _subscriptionService.GetSubscriptionForUser(userId.Value);
            return Ok(subscription);
        }

        /// <summary>
        /// Create a Stripe Checkout session for upgrading to Pro.
        /// </summary>
        [HttpPost("checkout")]
        public async Task<ActionResult<CheckoutSessionResponseDto>> CreateCheckoutSession(
            [FromBody] CreateCheckoutSessionDto dto)
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
                return Unauthorized();

            try
            {
                var origin = Request.Headers["Origin"].ToString();
                if (string.IsNullOrEmpty(origin))
                    origin = $"{Request.Scheme}://{Request.Host}";

                var successUrl = $"{origin}/subscription/success?session_id={{CHECKOUT_SESSION_ID}}";
                var cancelUrl = $"{origin}/subscription/canceled";

                var url = await _subscriptionService.CreateCheckoutSession(
                    userId.Value, dto.PlanId, successUrl, cancelUrl);

                return Ok(new CheckoutSessionResponseDto { Url = url });
            }
            catch (SubscriptionException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary>
        /// Create a Stripe Customer Portal session for managing billing.
        /// </summary>
        [HttpPost("portal")]
        public async Task<ActionResult<PortalSessionResponseDto>> CreatePortalSession()
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
                return Unauthorized();

            try
            {
                var origin = Request.Headers["Origin"].ToString();
                if (string.IsNullOrEmpty(origin))
                    origin = $"{Request.Scheme}://{Request.Host}";

                var returnUrl = $"{origin}/settings";
                var url = await _subscriptionService.CreateCustomerPortalSession(userId.Value, returnUrl);
                return Ok(new PortalSessionResponseDto { Url = url });
            }
            catch (SubscriptionException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary>
        /// Stripe webhook endpoint. Validates the Stripe-Signature header and processes events.
        /// </summary>
        [HttpPost("webhook")]
        [AllowAnonymous]
        // Exempt from the anonymous IP budget. Stripe delivers every event for every customer from
        // a small pool of its own addresses, so the whole webhook stream shares one partition — a
        // billing burst would otherwise 429 itself. Authenticity is already established by the
        // Stripe-Signature check below, which is the control that matters here.
        [DisableRateLimiting]
        public async Task<IActionResult> HandleWebhook()
        {
            var json = await new StreamReader(HttpContext.Request.Body, Encoding.UTF8).ReadToEndAsync();
            var signature = Request.Headers["Stripe-Signature"].ToString();

            try
            {
                await _subscriptionService.HandleStripeWebhook(json, signature);
                return Ok();
            }
            catch (Stripe.StripeException ex)
            {
                _telemetry.TrackException(ex);
                return BadRequest($"Webhook error: {ex.Message}");
            }
        }
    }

    public class CheckoutSessionResponseDto
    {
        public string? Url { get; set; }
    }

    public class PortalSessionResponseDto
    {
        public string? Url { get; set; }
    }
}
