using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace PrintLogApi.Webhooks;

/// <summary>
/// One print event waiting for delivery. It carries ids only: the payload is built from the
/// database when it is delivered, scoped to <see cref="UserId"/>.
/// </summary>
public sealed record WebhookEvent(string EventId, string EventType, long UserId, long PrintId, DateTimeOffset OccurredAt);

/// <summary>The in-process queue between the request path and <see cref="WebhookDeliveryService"/>.</summary>
public sealed class WebhookEventQueue(IOptions<WebhookOptions> options)
{
    private readonly Channel<WebhookEvent> _channel = Channel.CreateBounded<WebhookEvent>(
        new BoundedChannelOptions(options.Value.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    public ChannelReader<WebhookEvent> Reader => _channel.Reader;

    /// <summary>False when the queue is full and the event was dropped.</summary>
    public bool TryWrite(WebhookEvent webhookEvent) => _channel.Writer.TryWrite(webhookEvent);
}

/// <summary>Hands print events to the webhook pipeline (#235). Never blocks and never throws.</summary>
public interface IWebhookDispatcher
{
    void Enqueue(string eventType, long userId, long printId);
}

public sealed class WebhookDispatcher(
    WebhookEventQueue queue,
    TimeProvider clock,
    ILogger<WebhookDispatcher> logger) : IWebhookDispatcher
{
    public void Enqueue(string eventType, long userId, long printId)
    {
        try
        {
            var webhookEvent = new WebhookEvent("evt_" + Guid.NewGuid().ToString("N"), eventType, userId, printId, clock.GetUtcNow());
            if (!queue.TryWrite(webhookEvent))
            {
                logger.LogWarning("The webhook queue is full; dropped {EventType} for print {PrintId}.", eventType, printId);
            }
        }
        catch (Exception ex)
        {
            // The print is already saved; a webhook must never fail the request that saved it.
            logger.LogError(ex, "Could not queue {EventType} for print {PrintId}.", eventType, printId);
        }
    }
}
