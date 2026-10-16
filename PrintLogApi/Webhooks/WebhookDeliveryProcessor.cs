using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrintLogApi.Email;
using PrintLogApi.Models;
using PrintLogApi.Services;

namespace PrintLogApi.Webhooks;

/// <summary>
/// Delivers one queued event to every enabled subscription of its user that selected it (#235).
/// </summary>
/// <remarks>
/// The body is built once, from the print loaded by id <b>and</b> owner, so a webhook can only ever
/// carry its own user's data. Each subscription then gets up to <c>1 + RetryDelays.Length</c>
/// attempts. The outcome is written with single-statement updates, so two events finishing at once
/// cannot lose a failure, and only the update that actually turns a subscription off notifies.
/// </remarks>
public sealed class WebhookDeliveryProcessor(
    PrintLogContext context,
    WebhookSender sender,
    WebhookSecretProtector protector,
    INotificationService notificationService,
    IOptions<WebhookOptions> options,
    IOptions<EmailOptions> emailOptions,
    TimeProvider clock,
    ILogger<WebhookDeliveryProcessor> logger)
{
    private readonly WebhookOptions _options = options.Value;

    public async Task ProcessAsync(WebhookEvent webhookEvent, CancellationToken ct)
    {
        if (!protector.IsConfigured)
        {
            return;
        }

        var flag = WebhookEventTypes.ToFlag(webhookEvent.EventType);
        var subscriptions = await context.WebhookSubscriptions.AsNoTracking()
            .Where(w => w.UserId == webhookEvent.UserId && w.Enabled && (w.Events & flag) == flag)
            .ToListAsync(ct);
        if (flag == WebhookEvents.None || subscriptions.Count == 0)
        {
            return;
        }

        var body = await BuildBody(webhookEvent, ct);
        if (body is null)
        {
            // Deleted before it could be delivered; there is nothing left to describe.
            return;
        }
        var message = new WebhookMessage(webhookEvent.EventType, webhookEvent.EventId, body);

        foreach (var subscription in subscriptions)
        {
            await Deliver(subscription, message, ct);
        }
    }

    private async Task Deliver(WebhookSubscription subscription, WebhookMessage message, CancellationToken ct)
    {
        string secret;
        try
        {
            secret = protector.Unprotect(subscription.Id, subscription.EncryptedSecret);
        }
        catch (CryptographicException)
        {
            // Its key was removed from configuration. Rotating the secret recovers it.
            logger.LogWarning("Webhook {SubscriptionId}: the secret cannot be decrypted with the configured keys.", subscription.Id);
            return;
        }

        var url = new Uri(subscription.Url);
        WebhookAttemptResult result;
        var attempt = 1;
        while (true)
        {
            result = await sender.SendAsync(url, secret, message, attempt, ct);
            if (result.Succeeded || !result.Retryable || attempt > _options.EffectiveRetryDelays.Length)
            {
                break;
            }
            var delay = _options.EffectiveRetryDelays[attempt - 1];
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, clock, ct);
            }
            attempt++;
        }

        await Record(subscription, url.Host, result, ct);
    }

    private async Task Record(WebhookSubscription subscription, string host, WebhookAttemptResult result, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var rows = context.WebhookSubscriptions.Where(w => w.Id == subscription.Id);

        if (result.Succeeded)
        {
            await rows.ExecuteUpdateAsync(s => s
                .SetProperty(w => w.ConsecutiveFailures, 0)
                .SetProperty(w => w.LastDeliveryAt, now)
                .SetProperty(w => w.LastStatusCode, result.StatusCode)
                .SetProperty(w => w.LastDeliveryResult, result.Result), ct);
            return;
        }

        await rows.ExecuteUpdateAsync(s => s
            .SetProperty(w => w.ConsecutiveFailures, w => w.ConsecutiveFailures + 1)
            .SetProperty(w => w.LastDeliveryAt, now)
            .SetProperty(w => w.LastStatusCode, result.StatusCode)
            .SetProperty(w => w.LastDeliveryResult, result.Result), ct);

        var max = _options.MaxConsecutiveFailures;
        var disabled = await rows
            .Where(w => w.Enabled && w.ConsecutiveFailures >= max)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Enabled, false)
                .SetProperty(w => w.AutoDisabledAt, now), ct);
        if (disabled == 0)
        {
            return;
        }

        logger.LogWarning("Webhook {SubscriptionId} to {Host} was turned off after {Failures} failed deliveries in a row.",
            subscription.Id, host, max);
        try
        {
            // The host only: a Home Assistant webhook URL's path is its secret.
            await notificationService.CreateNotification(
                subscription.UserId,
                NotificationType.SystemAnnouncement,
                "Webhook turned off",
                $"Your webhook to {host} was turned off after {max} failed deliveries in a row. Check that it is reachable, then turn it back on.",
                actionUrl: "/settings/webhooks");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not notify the owner of webhook {SubscriptionId} that it was turned off.", subscription.Id);
        }
    }

    private async Task<byte[]?> BuildBody(WebhookEvent webhookEvent, CancellationToken ct)
    {
        var print = await context.Prints.AsNoTracking()
            .Where(p => p.Id == webhookEvent.PrintId && p.CreatedById == webhookEvent.UserId)
            .Select(p => new
            {
                p.Id,
                p.Title,
                p.Status,
                p.StartDate,
                p.PrintTimeInSeconds,
                p.EstimatedPrintTimeInSeconds,
                PrinterId = p.Printer.Id,
                PrinterName = p.Printer.Name,
                Filament = p.FilamentUsage!.Select(f => new
                {
                    f.FilamentId,
                    Name = f.Filament != null ? f.Filament.DisplayName : null,
                    Brand = f.Filament != null ? f.Filament.Brand : null,
                    MaterialType = f.Filament != null ? f.Filament.MaterialType : null,
                    ColorHex = f.Filament != null ? f.Filament.ColorHex : null,
                    f.Slot,
                    f.AmountMg,
                    f.EstimatedAmountMg,
                }).ToList(),
            })
            .FirstOrDefaultAsync(ct);
        if (print is null)
        {
            return null;
        }

        var duration = print.PrintTimeInSeconds is > 0 ? print.PrintTimeInSeconds : null;
        DateTimeOffset? finishedAt = null;
        if (webhookEvent.EventType != WebhookEventTypes.PrintStarted)
        {
            finishedAt = print.StartDate is { } start && duration is { } seconds
                ? start.AddSeconds(seconds)
                : webhookEvent.OccurredAt;
        }

        var webBase = emailOptions.Value.WebBaseUrl?.TrimEnd('/');
        var data = new WebhookPrintEventData(
            new WebhookPrintData(
                print.Id,
                print.Title,
                print.Status.ToString(),
                print.StartDate,
                finishedAt,
                duration,
                print.EstimatedPrintTimeInSeconds is > 0 ? print.EstimatedPrintTimeInSeconds : null,
                string.IsNullOrEmpty(webBase) ? null : $"{webBase}/prints/{print.Id}"),
            new WebhookPrinterData(print.PrinterId, print.PrinterName),
            print.Filament
                .OrderBy(f => f.Slot ?? int.MaxValue)
                .Select(f => new WebhookFilamentData(
                    f.FilamentId, f.Name, f.Brand, f.MaterialType, f.ColorHex, f.Slot,
                    ToGrams(f.AmountMg), ToGrams(f.EstimatedAmountMg)))
                .ToList());

        var payload = new WebhookPayload<WebhookPrintEventData>(
            WebhookPayload<WebhookPrintEventData>.CurrentVersion, webhookEvent.EventId, webhookEvent.EventType, webhookEvent.OccurredAt, data);
        return JsonSerializer.SerializeToUtf8Bytes(payload, WebhookJsonContext.Default.WebhookPayloadWebhookPrintEventData);

        static double? ToGrams(int? mg) => mg is { } value ? Math.Round(value / 1000.0, 3) : null;
    }
}
