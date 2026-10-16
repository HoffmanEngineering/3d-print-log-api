using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace PrintLogApi.Webhooks;

/// <summary>
/// Delivers queued webhook events off the request path (#235).
/// </summary>
/// <remarks>
/// <para>
/// One loop fans each event out into deliveries (one per subscription) on an internal queue, and
/// <see cref="WebhookOptions.MaxConcurrentDeliveries"/> workers take single attempts off it. The
/// shape exists so that no receiver can starve the others:
/// </para>
/// <list type="bullet">
/// <item>A worker holds its slot for one attempt only. A retry waits on a timer and rejoins the
/// queue, so backoff costs no worker.</item>
/// <item>At most <see cref="WebhookOptions.MaxInFlightPerUser"/> attempts per user and
/// <see cref="WebhookOptions.MaxInFlightPerHost"/> per host run at once. A delivery over either cap
/// rejoins the queue after a short wait instead of occupying a worker, so one user's blackholed URL,
/// or an outage at a host many users share, takes two workers, not all of them.</item>
/// <item>A subscription failing repeatedly has its circuit opened in
/// <see cref="WebhookDeliveryProcessor.PrepareAsync"/>, so it stops producing work at all.</item>
/// </list>
/// <para>
/// Everything is in process: stopping cancels attempts in flight and drops what is waiting.
/// </para>
/// </remarks>
public sealed class WebhookDeliveryService(
    WebhookEventQueue events,
    IServiceScopeFactory scopeFactory,
    IOptions<WebhookOptions> options,
    ILogger<WebhookDeliveryService> logger) : BackgroundService
{
    /// <summary>How long a delivery over a per-user or per-host cap waits before it is tried again.</summary>
    private static readonly TimeSpan CapacityRecheck = TimeSpan.FromMilliseconds(200);

    private readonly WebhookOptions _options = options.Value;

    private readonly Channel<WebhookDelivery> _ready = Channel.CreateBounded<WebhookDelivery>(
        new BoundedChannelOptions(options.Value.DeliveryQueueCapacity) { FullMode = BoundedChannelFullMode.DropWrite });

    private readonly Dictionary<string, int> _inFlight = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, _options.MaxConcurrentDeliveries)
            .Select(_ => Task.Run(() => Work(stoppingToken), CancellationToken.None))
            .ToList();

        try
        {
            await foreach (var webhookEvent in events.Reader.ReadAllAsync(stoppingToken))
            {
                await FanOut(webhookEvent, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        await Task.WhenAll(workers);
    }

    private async Task FanOut(WebhookEvent webhookEvent, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var deliveries = await scope.ServiceProvider.GetRequiredService<WebhookDeliveryProcessor>()
                .PrepareAsync(webhookEvent, stoppingToken);
            foreach (var delivery in deliveries)
            {
                Enqueue(delivery);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Webhook event {EventId} could not be prepared.", webhookEvent.EventId);
        }
    }

    private async Task Work(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var delivery in _ready.Reader.ReadAllAsync(stoppingToken))
            {
                var keys = Keys(delivery);
                if (!TryAcquire(keys))
                {
                    Later(delivery, CapacityRecheck, stoppingToken);
                    continue;
                }

                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var outcome = await scope.ServiceProvider.GetRequiredService<WebhookDeliveryProcessor>()
                        .AttemptAsync(delivery, stoppingToken);
                    if (outcome.Next is { } next)
                    {
                        Later(next, outcome.Delay, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Webhook {SubscriptionId} delivery of {EventId} failed.",
                        delivery.SubscriptionId, delivery.Message.EventId);
                }
                finally
                {
                    Release(keys);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void Enqueue(WebhookDelivery delivery)
    {
        if (!_ready.Writer.TryWrite(delivery))
        {
            logger.LogWarning("The webhook delivery queue is full; dropped {EventId} for webhook {SubscriptionId}.",
                delivery.Message.EventId, delivery.SubscriptionId);
        }
    }

    /// <summary>Puts <paramref name="delivery"/> back on the queue after <paramref name="delay"/>, holding no worker meanwhile.</summary>
    private void Later(WebhookDelivery delivery, TimeSpan delay, CancellationToken stoppingToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            Enqueue(delivery);
            return;
        }

        _ = Task.Delay(delay, stoppingToken).ContinueWith(
            t =>
            {
                if (!t.IsCanceled)
                {
                    Enqueue(delivery);
                }
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static (string User, string Host) Keys(WebhookDelivery delivery) =>
        ($"user:{delivery.UserId}", $"host:{delivery.Host}");

    private bool TryAcquire((string User, string Host) keys)
    {
        lock (_inFlight)
        {
            if (_inFlight.GetValueOrDefault(keys.User) >= _options.MaxInFlightPerUser
                || _inFlight.GetValueOrDefault(keys.Host) >= _options.MaxInFlightPerHost)
            {
                return false;
            }
            _inFlight[keys.User] = _inFlight.GetValueOrDefault(keys.User) + 1;
            _inFlight[keys.Host] = _inFlight.GetValueOrDefault(keys.Host) + 1;
            return true;
        }
    }

    private void Release((string User, string Host) keys)
    {
        lock (_inFlight)
        {
            Decrement(keys.User);
            Decrement(keys.Host);
        }

        void Decrement(string key)
        {
            var count = _inFlight.GetValueOrDefault(key) - 1;
            if (count <= 0)
            {
                _inFlight.Remove(key);
            }
            else
            {
                _inFlight[key] = count;
            }
        }
    }
}
