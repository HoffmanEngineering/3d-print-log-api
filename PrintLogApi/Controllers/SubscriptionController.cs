using System.Text;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using PrintLogApi.Authentication.Devices;
using PrintLogApi.Email;
using PrintLogApi.Exceptions;
using PrintLogApi.Extensions;
using PrintLogApi.Models.DTOs.Subscription;
using PrintLogApi.Models.Stripe;
using PrintLogApi.Services;

namespace PrintLogApi.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class SubscriptionController(
    ISubscriptionService subscriptionService,
    TelemetryClient telemetry,
    IOptions<EmailOptions> emailOptions,
    IOptions<StripeOptions> stripeOptions,
    ILogger<SubscriptionController> logger) : ControllerBase
{
    /// <summary>
    /// The largest webhook body read. Stripe's event payloads are a few KB to tens of KB (it
    /// truncates long lists in events), and the body is read before its signature can be checked,
    /// on an anonymous route with no rate limit, so the server default of about 28.6 MiB is far too
    /// generous.
    /// </summary>
    public const int MaxWebhookBodyBytes = 1024 * 1024;

    /// <summary>
    /// Get the current user's subscription status.
    /// </summary>
    [DeviceAccess]
    [HttpGet("me")]
    public async Task<ActionResult<SubscriptionDto>> GetCurrentUserSubscription()
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized();

        var subscription = await subscriptionService.GetSubscriptionForUser(userId.Value);
        return Ok(subscription);
    }

    /// <summary>
    /// Create a Stripe Checkout session for upgrading to Pro. Interactive sessions only.
    /// </summary>
    [HttpPost("checkout")]
    [Authorize(Policy = "InteractiveUserOnly")]
    public async Task<ActionResult<CheckoutSessionResponseDto>> CreateCheckoutSession(
        [FromBody] CreateCheckoutSessionDto dto)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized();

        var returnBase = ReturnBase();
        if (returnBase is null)
            return ReturnBaseMissing();

        try
        {
            var successUrl = $"{returnBase}/subscription/success?session_id={{CHECKOUT_SESSION_ID}}";
            var cancelUrl = $"{returnBase}/subscription/canceled";

            var url = await subscriptionService.CreateCheckoutSession(
                userId.Value, dto.PlanId, successUrl, cancelUrl);

            return Ok(new CheckoutSessionResponseDto { Url = url });
        }
        catch (SubscriptionException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Create a Stripe Customer Portal session for managing billing. Interactive sessions only:
    /// the portal can cancel the subscription and change the payment method.
    /// </summary>
    [HttpPost("portal")]
    [Authorize(Policy = "InteractiveUserOnly")]
    public async Task<ActionResult<PortalSessionResponseDto>> CreatePortalSession()
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized();

        var returnBase = ReturnBase();
        if (returnBase is null)
            return ReturnBaseMissing();

        try
        {
            var returnUrl = $"{returnBase}/settings";
            var url = await subscriptionService.CreateCustomerPortalSession(userId.Value, returnUrl);
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
    [RequestSizeLimit(MaxWebhookBodyBytes)]
    public async Task<IActionResult> HandleWebhook()
    {
        // Checked before the body is read. With a blank secret Stripe.net accepts a payload
        // signed with an empty key, so anyone could post billing events. Without a secret the
        // webhook is off; Production also logs that at Critical on startup (StripeWebhookSecretCheck).
        if (string.IsNullOrWhiteSpace(stripeOptions.Value.WebhookSecret))
        {
            logger.LogWarning("Refused a Stripe webhook: Stripe:WebhookSecret is not configured.");
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Stripe webhooks are not configured.");
        }

        var json = await new StreamReader(HttpContext.Request.Body, Encoding.UTF8).ReadToEndAsync();
        var signature = Request.Headers["Stripe-Signature"].ToString();

        try
        {
            await subscriptionService.HandleStripeWebhook(json, signature);
            return Ok();
        }
        catch (Stripe.StripeException ex)
        {
            telemetry.TrackException(ex);
            return BadRequest($"Webhook error: {ex.Message}");
        }
    }

    /// <summary>
    /// Where Stripe sends the customer back to: <c>Email:WebBaseUrl</c>, or the request's
    /// <c>Origin</c> when it is on <c>Stripe:AllowedReturnOrigins</c>. Null when the web base is not
    /// configured. Never the request's Host.
    /// </summary>
    private string? ReturnBase() => StripeReturnUrls.ResolveBase(
        Request.Headers.Origin.ToString(),
        emailOptions.Value.WebBaseUrl,
        stripeOptions.Value.AllowedReturnOrigins);

    private ObjectResult ReturnBaseMissing()
    {
        logger.LogError("Refused a Stripe session: Email:WebBaseUrl is not an absolute http(s) URL, so there is nowhere safe to return the customer to.");
        return Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Billing is not configured.");
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
