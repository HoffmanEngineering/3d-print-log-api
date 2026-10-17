using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PrintLogApi.Extensions;
using PrintLogApi.Models.DTOs.Webhook;
using PrintLogApi.Webhooks;

namespace PrintLogApi.Controllers;

/// <summary>
/// Outbound webhooks for print events, such as a Home Assistant webhook trigger.
/// </summary>
/// <remarks>
/// Each subscription receives <c>print.started</c>, <c>print.finished</c> and/or
/// <c>print.failed</c> as a signed JSON <c>POST</c>. The signature is
/// <c>X-PrintLog-Signature: t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256 of t + "." + body&gt;</c>, keyed
/// with the subscription's secret; <c>X-PrintLog-Event-Id</c> identifies the event across retries.
/// Only a signed-in user can manage webhooks: an API key cannot read or mint a signing secret.
/// </remarks>
[Route("api/[controller]")]
[ApiController]
[Authorize(Policy = "InteractiveUserOnly")]
public class WebhooksController(IWebhookSubscriptionService webhooks) : ControllerBase
{
    /// <summary>
    /// The test endpoint's own budget, per user (<c>Webhooks:TestRateLimitPerMinute</c>, 5): each
    /// call is an outbound request on demand, which must not become a scanner.
    /// </summary>
    public const string TestRateLimitPolicy = "webhook-test";

    /// <summary>
    /// Create, update and rotate share a budget per user (<c>Webhooks:WriteRateLimitPerMinute</c>,
    /// 20): each create or URL change resolves a host the caller chose.
    /// </summary>
    public const string WriteRateLimitPolicy = "webhook-write";

    /// <summary>
    /// List your webhooks.
    /// </summary>
    /// <remarks>Oldest first, with each one's last delivery. Never includes a secret.</remarks>
    /// <response code="200">Your webhooks.</response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<List<WebhookSubscriptionDto>>> GetWebhooks()
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }
        return Ok(await webhooks.ListAsync(userId, HttpContext.RequestAborted));
    }

    /// <summary>
    /// Get one webhook.
    /// </summary>
    /// <param name="id">The webhook's id.</param>
    /// <response code="200">The webhook, without its secret.</response>
    /// <response code="404">The webhook is not yours.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WebhookSubscriptionDto>> GetWebhook(Guid id)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }
        return await webhooks.GetAsync(userId, id, HttpContext.RequestAborted) is { } webhook ? Ok(webhook) : NotFound();
    }

    /// <summary>
    /// Create a webhook.
    /// </summary>
    /// <remarks>
    /// Returns the signing secret once; it cannot be shown again (rotate it for a new one). The URL
    /// must be http or https on a public address: private, loopback, link-local and reserved
    /// addresses are refused with <c>code: "blocked_webhook_address"</c>. Events default to all three.
    /// The Free plan includes one webhook and Pro ten; one more returns 403 with
    /// <c>code: "webhook_limit"</c> and the <c>limit</c>.
    /// </remarks>
    /// <response code="201">The new webhook, with its secret.</response>
    /// <response code="400">The URL or events are not valid.</response>
    /// <response code="403">Your plan's webhooks are all in use.</response>
    /// <response code="503">Webhooks are not configured on this server.</response>
    [HttpPost]
    [EnableRateLimiting(WriteRateLimitPolicy)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<WebhookSubscriptionCreatedDto>> CreateWebhook(SaveWebhookSubscriptionDto dto)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }
        return await Handle(async () =>
            StatusCode(StatusCodes.Status201Created, await webhooks.CreateAsync(userId, dto, HttpContext.RequestAborted)));
    }

    /// <summary>
    /// Update a webhook.
    /// </summary>
    /// <remarks>
    /// Replaces the URL, events and description; <c>enabled</c> is left as it is when omitted.
    /// Turning a webhook that failures switched off back on resets its failure count. The secret
    /// does not change.
    /// </remarks>
    /// <param name="id">The webhook's id.</param>
    /// <param name="dto">The new settings.</param>
    /// <response code="200">The updated webhook.</response>
    /// <response code="400">The URL or events are not valid.</response>
    /// <response code="404">The webhook is not yours.</response>
    [HttpPut("{id:guid}")]
    [EnableRateLimiting(WriteRateLimitPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<WebhookSubscriptionDto>> UpdateWebhook(Guid id, SaveWebhookSubscriptionDto dto)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }
        return await Handle(async () =>
            await webhooks.UpdateAsync(userId, id, dto, HttpContext.RequestAborted) is { } updated ? Ok(updated) : NotFound());
    }

    /// <summary>
    /// Delete a webhook.
    /// </summary>
    /// <param name="id">The webhook's id.</param>
    /// <response code="204">Deleted.</response>
    /// <response code="404">The webhook is not yours.</response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteWebhook(Guid id)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }
        return await webhooks.DeleteAsync(userId, id, HttpContext.RequestAborted) ? NoContent() : NotFound();
    }

    /// <summary>
    /// Rotate a webhook's signing secret.
    /// </summary>
    /// <remarks>Returns the new secret once. The old one stops signing at once.</remarks>
    /// <param name="id">The webhook's id.</param>
    /// <response code="200">The webhook, with its new secret.</response>
    /// <response code="404">The webhook is not yours.</response>
    [HttpPost("{id:guid}/rotate-secret")]
    [EnableRateLimiting(WriteRateLimitPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<WebhookSubscriptionCreatedDto>> RotateWebhookSecret(Guid id)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }
        return await Handle(async () =>
            await webhooks.RotateSecretAsync(userId, id, HttpContext.RequestAborted) is { } rotated ? Ok(rotated) : NotFound());
    }

    /// <summary>
    /// Send a test event to a webhook.
    /// </summary>
    /// <remarks>
    /// Sends one signed <c>webhook.test</c> event now, whether or not the webhook is enabled, and
    /// reports what the receiver answered. One attempt, no retries, and it does not count toward the
    /// webhook's failures.
    /// </remarks>
    /// <param name="id">The webhook's id.</param>
    /// <response code="200">What happened to the test event.</response>
    /// <response code="404">The webhook is not yours.</response>
    [HttpPost("{id:guid}/test")]
    [EnableRateLimiting(TestRateLimitPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<WebhookTestResultDto>> TestWebhook(Guid id)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }
        return await Handle(async () =>
            await webhooks.SendTestAsync(userId, id, HttpContext.RequestAborted) is { } result ? Ok(result) : NotFound());
    }

    private async Task<ActionResult> Handle(Func<Task<ActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (WebhooksUnavailableException ex)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "Webhooks unavailable", ex.Message, "webhooks_unavailable");
        }
        catch (WebhookValidationException ex)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid webhook", ex.Message, ex.Code);
        }
        catch (WebhookLimitException ex)
        {
            return Problem(StatusCodes.Status403Forbidden, "Webhook limit reached", ex.Message, "webhook_limit", ex.Limit);
        }
    }

    private ObjectResult Problem(int status, string title, string detail, string code, int? limit = null)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, statusCode: status, title: title, detail: detail);
        problem.Extensions["code"] = code;
        if (limit is { } value)
        {
            problem.Extensions["limit"] = value;
        }
        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
    }
}
