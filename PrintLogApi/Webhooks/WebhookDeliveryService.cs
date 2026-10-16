using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace PrintLogApi.Webhooks;

/// <summary>
/// Delivers queued webhook events off the request path (#235).
/// </summary>
/// <remarks>
/// <para>
/// One loop fans each event out into deliveries (one per subscription), and a scheduler runs single
/// attempts of them. Its rules, all held under one lock:
/// </para>
/// <list type="bullet">
/// <item><b>FIFO per subscription.</b> Each subscription has a lane; only its head is eligible, and
/// the next delivery waits until the head is done, retries included. A receiver sees
/// <c>print.started</c> before <c>print.finished</c> even when the first needed a retry.</item>
/// <item><b>Caps.</b> At most <see cref="WebhookOptions.MaxConcurrentDeliveries"/> attempts in
/// flight in all, <see cref="WebhookOptions.MaxInFlightPerUser"/> per user and
/// <see cref="WebhookOptions.MaxInFlightPerHost"/> per host. The host cap is set well above the user
/// cap, because many users share one host (every Home Assistant Cloud user posts to
/// hooks.nabu.casa): a few users' hung instances there must not hold that host for everyone.</item>
/// <item><b>No polling.</b> A delivery over a cap joins a waiting list for the key that stopped it,
/// and releasing that key starts the next waiter. One that waited longer than
/// <see cref="WebhookOptions.MaxDeliveryWait"/> is dropped instead.</item>
/// <item><b>Retries hold nothing.</b> The head stays at the front of its lane while a timer waits
/// out the delay, using no worker and no cap.</item>
/// <item><b>Bounded.</b> At most <see cref="WebhookOptions.DeliveryQueueCapacity"/> deliveries are
/// held (waiting, in flight or between retries); past that a new one is dropped.</item>
/// </list>
/// <para>
/// A dropped delivery is recorded on its subscription as <c>dropped</c> so the owner can see it,
/// without counting as a failure (it is our capacity, not their receiver). Those writes go through
/// one recorder loop, one at a time and coalesced per subscription, so a burst of drops under
/// overload costs one write per subscription instead of a scope and a connection per drop.
/// Everything is in process: stopping cancels attempts in flight, ends the recorder, and forgets
/// what is waiting.
/// </para>
/// </remarks>
public sealed class WebhookDeliveryService(
    WebhookEventQueue events,
    IServiceScopeFactory scopeFactory,
    IOptions<WebhookOptions> options,
    TimeProvider clock,
    ILogger<WebhookDeliveryService> logger) : BackgroundService
{
    private const string AllKey = "*";

    private readonly WebhookOptions _options = options.Value;
    private readonly object _gate = new();

    // Everything below is guarded by _gate.
    private readonly Dictionary<Guid, Queue<WebhookDelivery>> _lanes = [];
    private readonly Dictionary<string, int> _inFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<Waiting>> _waiting = new(StringComparer.Ordinal);
    private int _held;
    private int _running;
    private bool _stopping;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CancellationToken _stoppingToken;

    // Drops waiting to be recorded: the latest drop time per subscription, and a signal for each
    // subscription that became pending. The channel holds at most one entry per subscription with
    // a pending drop, so it is bounded by the number of subscriptions, not by the number of drops.
    private readonly ConcurrentDictionary<Guid, DateTime> _pendingDrops = new();
    private readonly Channel<Guid> _dropSignals = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>A delivery that is eligible but over a cap, and since when it has been eligible.</summary>
    private sealed record Waiting(WebhookDelivery Delivery, DateTimeOffset Since);

    /// <summary>What a pass under the lock decided, carried out after it is released.</summary>
    private sealed class Actions
    {
        public List<WebhookDelivery> Start { get; } = [];

        public List<(WebhookDelivery Delivery, string Reason)> Drop { get; } = [];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        var recorder = RecordDrops(stoppingToken);
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

        lock (_gate)
        {
            _stopping = true;
            if (_running == 0)
            {
                _drained.TrySetResult();
            }
        }
        await _drained.Task;
        _dropSignals.Writer.TryComplete();
        await recorder;
    }

    private async Task FanOut(WebhookEvent webhookEvent, CancellationToken stoppingToken)
    {
        IReadOnlyList<WebhookDelivery> deliveries;
        try
        {
            using var scope = scopeFactory.CreateScope();
            deliveries = await scope.ServiceProvider.GetRequiredService<WebhookDeliveryProcessor>()
                .PrepareAsync(webhookEvent, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Webhook event {EventId} could not be prepared.", webhookEvent.EventId);
            return;
        }

        var actions = new Actions();
        lock (_gate)
        {
            foreach (var delivery in deliveries)
            {
                Admit(delivery, actions);
            }
        }
        Carry(actions);
    }

    private void Admit(WebhookDelivery delivery, Actions actions)
    {
        if (_held >= _options.DeliveryQueueCapacity)
        {
            actions.Drop.Add((delivery, "the delivery queue is full"));
            return;
        }
        _held++;
        if (!_lanes.TryGetValue(delivery.SubscriptionId, out var lane))
        {
            _lanes[delivery.SubscriptionId] = lane = new Queue<WebhookDelivery>();
        }
        lane.Enqueue(delivery);
        if (lane.Count == 1)
        {
            TryStart(new Waiting(delivery, clock.GetUtcNow()), actions);
        }
    }

    /// <summary>Starts <paramref name="waiting"/> if every cap allows, else parks it on the key that does not.</summary>
    private void TryStart(Waiting waiting, Actions actions)
    {
        if (_stopping)
        {
            return;
        }
        if (clock.GetUtcNow() - waiting.Since > _options.MaxDeliveryWait)
        {
            actions.Drop.Add((waiting.Delivery, "it waited too long for capacity"));
            Advance(waiting.Delivery.SubscriptionId, actions);
            return;
        }

        var keys = Keys(waiting.Delivery);
        foreach (var (key, cap) in keys)
        {
            if (_inFlight.GetValueOrDefault(key) >= cap)
            {
                if (!_waiting.TryGetValue(key, out var queue))
                {
                    _waiting[key] = queue = new Queue<Waiting>();
                }
                queue.Enqueue(waiting);
                return;
            }
        }
        foreach (var (key, _) in keys)
        {
            _inFlight[key] = _inFlight.GetValueOrDefault(key) + 1;
        }
        _running++;
        actions.Start.Add(waiting.Delivery);
    }

    /// <summary>The subscription's head is done: let go of it and make the next one eligible.</summary>
    private void Advance(Guid subscriptionId, Actions actions)
    {
        _held--;
        if (!_lanes.TryGetValue(subscriptionId, out var lane))
        {
            return;
        }
        lane.Dequeue();
        if (lane.Count == 0)
        {
            _lanes.Remove(subscriptionId);
            return;
        }
        TryStart(new Waiting(lane.Peek(), clock.GetUtcNow()), actions);
    }

    private void Release(WebhookDelivery delivery, Actions actions)
    {
        var keys = Keys(delivery);
        foreach (var (key, _) in keys)
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

        // Each freed key lets its waiters try again, in the order they arrived. A waiter now stopped
        // by another key moves to that key's list; one past its age is dropped.
        foreach (var (key, cap) in keys)
        {
            while (_inFlight.GetValueOrDefault(key) < cap && _waiting.TryGetValue(key, out var queue) && queue.Count > 0)
            {
                TryStart(queue.Dequeue(), actions);
            }
            if (_waiting.TryGetValue(key, out var empty) && empty.Count == 0)
            {
                _waiting.Remove(key);
            }
        }
    }

    private (string Key, int Cap)[] Keys(WebhookDelivery delivery) =>
    [
        (AllKey, _options.MaxConcurrentDeliveries),
        ($"user:{delivery.UserId}", _options.MaxInFlightPerUser),
        ($"host:{delivery.Host}", _options.MaxInFlightPerHost),
    ];

    /// <summary>Carries out what a pass under the lock decided: attempts to start, drops to record.</summary>
    private void Carry(Actions actions)
    {
        foreach (var delivery in actions.Start)
        {
            _ = Run(delivery);
        }
        foreach (var (delivery, reason) in actions.Drop)
        {
            logger.LogWarning("Webhook {SubscriptionId}: dropped {EventId} because {Reason}.",
                delivery.SubscriptionId, delivery.Message.EventId, reason);
            QueueDropRecord(delivery.SubscriptionId);
        }
    }

    private async Task Run(WebhookDelivery delivery)
    {
        await Task.Yield();
        WebhookAttemptOutcome outcome = WebhookAttemptOutcome.Done;
        var stopped = false;
        try
        {
            using var scope = scopeFactory.CreateScope();
            outcome = await scope.ServiceProvider.GetRequiredService<WebhookDeliveryProcessor>()
                .AttemptAsync(delivery, _stoppingToken);
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
        {
            stopped = true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Webhook {SubscriptionId} delivery of {EventId} failed.",
                delivery.SubscriptionId, delivery.Message.EventId);
        }

        var actions = new Actions();
        lock (_gate)
        {
            _running--;
            if (!stopped)
            {
                Release(delivery, actions);
                if (outcome.Next is { } next)
                {
                    // The retry takes the head's place in its lane, so nothing behind it can pass.
                    var lane = _lanes[delivery.SubscriptionId];
                    lane.Dequeue();
                    var rest = lane.ToArray();
                    lane.Clear();
                    lane.Enqueue(next);
                    foreach (var behind in rest)
                    {
                        lane.Enqueue(behind);
                    }
                }
                else
                {
                    Advance(delivery.SubscriptionId, actions);
                }
            }
            if (_stopping && _running == 0)
            {
                _drained.TrySetResult();
            }
        }
        Carry(actions);

        if (!stopped && outcome.Next is { } retry)
        {
            Retry(retry, outcome.Delay);
        }
    }

    /// <summary>Makes <paramref name="next"/> eligible after <paramref name="delay"/>, holding nothing meanwhile.</summary>
    private void Retry(WebhookDelivery next, TimeSpan delay)
    {
        _ = Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, clock, _stoppingToken).ContinueWith(
            t =>
            {
                if (t.IsCanceled)
                {
                    return;
                }
                var actions = new Actions();
                lock (_gate)
                {
                    TryStart(new Waiting(next, clock.GetUtcNow()), actions);
                }
                Carry(actions);
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Hands a drop to the recorder, merging it with one already waiting for the subscription.</summary>
    private void QueueDropRecord(Guid subscriptionId)
    {
        var at = clock.GetUtcNow().UtcDateTime;
        var added = false;
        _pendingDrops.AddOrUpdate(subscriptionId,
            _ => { added = true; return at; },
            (_, earlier) => at > earlier ? at : earlier);
        if (added)
        {
            _dropSignals.Writer.TryWrite(subscriptionId);
        }
    }

    /// <summary>The one loop that writes drops: one write at a time, the latest drop per subscription.</summary>
    private async Task RecordDrops(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var subscriptionId in _dropSignals.Reader.ReadAllAsync(stoppingToken))
            {
                if (!_pendingDrops.TryRemove(subscriptionId, out var droppedAt))
                {
                    continue;
                }
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<WebhookDeliveryProcessor>()
                        .RecordDroppedAsync(subscriptionId, droppedAt, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Could not record a dropped delivery to webhook {SubscriptionId}.", subscriptionId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
