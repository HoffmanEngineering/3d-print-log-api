using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrintLogApi.Email;
using PrintLogApi.Models;
using PrintLogApi.Services;

namespace PrintLogApi.Webhooks;

/// <summary>
/// One subscription's delivery of one event: a unit of work on its own, so a slow or dead receiver
/// holds up only its own deliveries. <see cref="Url"/> and <see cref="EncryptedSecret"/> are the
/// subscription as it was when the event was fanned out; every attempt checks they still hold.
/// <see cref="Probe"/> marks the one delivery that claimed an open circuit's probe.
/// </summary>
public sealed record WebhookDelivery(
    Guid SubscriptionId,
    long UserId,
    string Url,
    string EncryptedSecret,
    WebhookMessage Message,
    int Attempt,
    int MaxAttempts,
    bool Probe = false)
{
    public string Host => new Uri(Url).Host.ToLowerInvariant();
}

/// <summary>What an attempt left to do: <see cref="Next"/> after <see cref="Delay"/>, or nothing.</summary>
public sealed record WebhookAttemptOutcome(WebhookDelivery? Next, TimeSpan Delay)
{
    public static readonly WebhookAttemptOutcome Done = new(null, TimeSpan.Zero);
}

/// <summary>
/// Turns a queued event into deliveries and runs single attempts of them (#235). The scheduling
/// (concurrency, per-user and per-host caps, waiting out a retry without holding a slot) is
/// <see cref="WebhookDeliveryService"/>'s.
/// </summary>
/// <remarks>
/// The body is built once, from the print loaded by id <b>and</b> owner, so a webhook can only ever
/// carry its own user's data. The outcome is written with single-statement updates, so two events
/// finishing at once cannot lose a failure, and only the update that actually turns a subscription
/// off notifies.
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

    /// <summary>
    /// One delivery per enabled subscription of the event's user that selected it, all carrying the
    /// same body. A subscription whose circuit is open (<see cref="WebhookOptions.CircuitBreakerThreshold"/>
    /// failures in a row, the last within <see cref="WebhookOptions.CircuitBreakerCooldown"/>) is
    /// skipped. Once the cooldown has passed, one event claims a single probe with no retries.
    /// </summary>
    /// <remarks>
    /// The probe is claimed with one conditional update that moves <c>LastDeliveryAt</c> to now, so
    /// two events (or two instances) past the cooldown cannot both probe: the loser sees a fresh
    /// <c>LastDeliveryAt</c> and skips. The probe's own outcome overwrites it seconds later. A probe
    /// that never finishes (a restart) leaves the circuit open for one more cooldown.
    /// </remarks>
    public async Task<IReadOnlyList<WebhookDelivery>> PrepareAsync(WebhookEvent webhookEvent, CancellationToken ct)
    {
        var flag = WebhookEventTypes.ToFlag(webhookEvent.EventType);
        if (!protector.IsConfigured || flag == WebhookEvents.None)
        {
            return [];
        }

        var subscriptions = await context.WebhookSubscriptions.AsNoTracking()
            .Where(w => w.UserId == webhookEvent.UserId && w.Enabled && (w.Events & flag) == flag)
            .ToListAsync(ct);
        if (subscriptions.Count == 0)
        {
            return [];
        }

        var body = await BuildBody(webhookEvent, ct);
        if (body is null)
        {
            // Deleted before it could be delivered; there is nothing left to describe.
            return [];
        }
        var message = new WebhookMessage(webhookEvent.EventType, webhookEvent.EventId, body);

        var now = clock.GetUtcNow().UtcDateTime;
        var deliveries = new List<WebhookDelivery>();
        foreach (var subscription in subscriptions)
        {
            var maxAttempts = 1 + _options.EffectiveRetryDelays.Length;
            var probe = false;
            var threshold = _options.CircuitBreakerThreshold;
            if (subscription.ConsecutiveFailures >= threshold)
            {
                var cutoff = now - _options.CircuitBreakerCooldown;
                var claimed = await context.WebhookSubscriptions
                    .Where(w => w.Id == subscription.Id && w.ConsecutiveFailures >= threshold
                        && (w.LastDeliveryAt == null || w.LastDeliveryAt <= cutoff))
                    .ExecuteUpdateAsync(s => s.SetProperty(w => w.LastDeliveryAt, now), ct);
                if (claimed == 0)
                {
                    logger.LogInformation("Webhook {SubscriptionId}: circuit open after {Failures} failures; skipped {EventId}.",
                        subscription.Id, subscription.ConsecutiveFailures, webhookEvent.EventId);
                    continue;
                }
                probe = true;
                maxAttempts = 1;
            }
            deliveries.Add(new WebhookDelivery(subscription.Id, subscription.UserId, subscription.Url,
                subscription.EncryptedSecret, message, Attempt: 1, maxAttempts, probe));
        }
        return deliveries;
    }

    /// <summary>
    /// Sends one attempt of <paramref name="delivery"/>. Returns the next attempt and how long to
    /// wait first when the receiver may succeed later; otherwise records the outcome and is done.
    /// Every attempt, the first included, first checks that the subscription still exists, is on,
    /// and has the URL and secret it was fanned out with, and quietly stops when any of that
    /// changed: a delivery can wait in the queue long enough for the user to change or delete the
    /// webhook, or their whole account. It also stops when the subscription's circuit opened
    /// meanwhile, unless it is that circuit's probe.
    /// </summary>
    public async Task<WebhookAttemptOutcome> AttemptAsync(WebhookDelivery delivery, CancellationToken ct)
    {
        var current = await context.WebhookSubscriptions.AsNoTracking()
            .Where(w => w.Id == delivery.SubscriptionId)
            .Select(w => new { w.Enabled, w.Url, w.EncryptedSecret, w.ConsecutiveFailures })
            .FirstOrDefaultAsync(ct);
        if (current is null || !current.Enabled || current.Url != delivery.Url || current.EncryptedSecret != delivery.EncryptedSecret)
        {
            return WebhookAttemptOutcome.Done;
        }
        if (!delivery.Probe && current.ConsecutiveFailures >= _options.CircuitBreakerThreshold)
        {
            logger.LogInformation("Webhook {SubscriptionId}: circuit opened while {EventId} waited; skipped.",
                delivery.SubscriptionId, delivery.Message.EventId);
            return WebhookAttemptOutcome.Done;
        }

        string secret;
        try
        {
            secret = protector.Unprotect(delivery.SubscriptionId, delivery.EncryptedSecret);
        }
        catch (CryptographicException)
        {
            // Its key was removed from configuration. Recorded so the owner sees why; rotating the
            // secret recovers it.
            logger.LogWarning("Webhook {SubscriptionId}: the secret cannot be decrypted with the configured keys.", delivery.SubscriptionId);
            await Record(delivery, new WebhookAttemptResult(WebhookDeliveryResults.SecretUnavailable, null, Retryable: false), ct);
            return WebhookAttemptOutcome.Done;
        }

        var result = await sender.SendAsync(new Uri(delivery.Url), secret, delivery.Message, delivery.Attempt, ct);
        if (!result.Succeeded && result.Retryable && delivery.Attempt < delivery.MaxAttempts)
        {
            var delay = _options.EffectiveRetryDelays[Math.Min(delivery.Attempt, _options.EffectiveRetryDelays.Length) - 1];
            return new WebhookAttemptOutcome(delivery with { Attempt = delivery.Attempt + 1 }, delay);
        }

        await Record(delivery, result, ct);
        return WebhookAttemptOutcome.Done;
    }

    /// <summary>
    /// Records that the scheduler dropped <paramref name="delivery"/> without attempting it. It
    /// counts as a failure, like any delivery the receiver never got.
    /// </summary>
    public Task RecordDroppedAsync(WebhookDelivery delivery, CancellationToken ct) =>
        Record(delivery, new WebhookAttemptResult(WebhookDeliveryResults.Dropped, null, Retryable: false), ct);

    private async Task Record(WebhookDelivery delivery, WebhookAttemptResult result, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var rows = context.WebhookSubscriptions.Where(w => w.Id == delivery.SubscriptionId);

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

        var host = delivery.Host;
        logger.LogWarning("Webhook {SubscriptionId} to {Host} was turned off after {Failures} failed deliveries in a row.",
            delivery.SubscriptionId, host, max);
        try
        {
            // The host only: a Home Assistant webhook URL's path is its secret.
            await notificationService.CreateNotification(
                delivery.UserId,
                NotificationType.SystemAnnouncement,
                "Webhook turned off",
                $"Your webhook to {host} was turned off after {max} failed deliveries in a row. Check that it is reachable, then turn it back on.",
                actionUrl: "/settings/webhooks");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not notify the owner of webhook {SubscriptionId} that it was turned off.", delivery.SubscriptionId);
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
