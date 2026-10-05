using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using PrintLogApi.Email;
using PrintLogApi.Email.Events;

namespace PrintLogApi.Controllers;

/// <summary>
/// Receives SES bounce, complaint, delivery and reject events through SNS (spec §6.10). Anonymous,
/// so trust comes from the SNS signature and the topic ARN, both checked before anything else.
/// </summary>
[Route("api/email-events")]
[ApiController]
[AllowAnonymous]
[EnableRateLimiting(EmailRateLimiting.EventsPolicy)]
[ApiExplorerSettings(IgnoreApi = true)]
public class EmailEventsController(
    ISnsMessageVerifier verifier,
    SesEventProcessor processor,
    IHttpClientFactory httpClientFactory,
    IOptions<EmailOptions> options,
    TelemetryClient telemetry) : ControllerBase
{
    private const int MaxBodyBytes = 256 * 1024;

    [HttpPost("ses")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Ses(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);

        var topic = options.Value.Ses.EventsTopicArn;
        if (!verifier.TryParse(body, out var envelope) || string.IsNullOrEmpty(topic) || envelope.TopicArn != topic)
        {
            return BadRequest();
        }

        switch (envelope.Type)
        {
            case "SubscriptionConfirmation":
                return await ConfirmSubscriptionAsync(envelope.SubscribeUrl, ct);

            case "Notification":
                try
                {
                    await processor.ProcessAsync(envelope.Message ?? "", ct);
                    return Ok();
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // 500 so SNS retries (about an hour by default). Alerting keys off this event.
                    telemetry.TrackEvent("Email_WebhookFailed");
                    telemetry.TrackException(ex);
                    return StatusCode(StatusCodes.Status500InternalServerError);
                }

            default:
                return Ok();
        }
    }

    private async Task<IActionResult> ConfirmSubscriptionAsync(string? subscribeUrl, CancellationToken ct)
    {
        // The signature already proves SNS sent this, but the URL is still fetched from inside the
        // App Service network, so it must be an SNS endpoint and nothing else.
        if (!Uri.TryCreate(subscribeUrl, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps
            || !url.Host.StartsWith("sns.", StringComparison.OrdinalIgnoreCase)
            || !url.Host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest();
        }

        using var response = await httpClientFactory.CreateClient(EmailEventsConstants.SnsHttpClient).GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            telemetry.TrackEvent("Email_WebhookFailed", new Dictionary<string, string> { ["stage"] = "subscribe" });
            return StatusCode(StatusCodes.Status502BadGateway);
        }

        telemetry.TrackEvent("Email_WebhookSubscribed");
        return Ok();
    }
}
