using Microsoft.Extensions.Options;

namespace PrintLogApi.Webhooks;

/// <summary>
/// Delivers queued webhook events off the request path (#235). Each event runs in its own scope
/// through <see cref="WebhookDeliveryProcessor"/>, up to
/// <see cref="WebhookOptions.MaxConcurrentDeliveries"/> at a time, so one slow receiver waiting out
/// its retries does not hold up everyone else's.
/// </summary>
public sealed class WebhookDeliveryService(
    WebhookEventQueue queue,
    IServiceScopeFactory scopeFactory,
    IOptions<WebhookOptions> options,
    ILogger<WebhookDeliveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var slots = new SemaphoreSlim(options.Value.MaxConcurrentDeliveries);
        var running = new HashSet<Task>();

        try
        {
            await foreach (var webhookEvent in queue.Reader.ReadAllAsync(stoppingToken))
            {
                await slots.WaitAsync(stoppingToken);
                var task = Task.Run(async () =>
                {
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<WebhookDeliveryProcessor>().ProcessAsync(webhookEvent, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Webhook event {EventId} could not be delivered.", webhookEvent.EventId);
                    }
                    finally
                    {
                        slots.Release();
                    }
                }, CancellationToken.None);

                lock (running)
                {
                    running.Add(task);
                }
                _ = task.ContinueWith(t => { lock (running) { running.Remove(t); } }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        Task[] remaining;
        lock (running)
        {
            remaining = running.ToArray();
        }
        await Task.WhenAll(remaining);
    }
}
