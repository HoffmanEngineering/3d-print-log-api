using Microsoft.Extensions.Options;

namespace PrintLogApi.Email.Outbox;

/// <summary>
/// Runs <see cref="EmailDispatcher"/> every <see cref="EmailOptions.DispatchInterval"/>, and at
/// once whenever <see cref="EmailDispatchSignal"/> is woken. Single instance only (spec §6.12):
/// the daily cap and send-rate limit are per process.
/// </summary>
public sealed class EmailDispatcherService(
    IServiceScopeFactory scopeFactory,
    IOptions<EmailOptions> options,
    EmailDispatchSignal signal,
    TimeProvider timeProvider,
    ILogger<EmailDispatcherService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, timeProvider, stoppingToken);
            using var timer = new PeriodicTimer(options.Value.DispatchInterval, timeProvider);

            // Both waits stay pending across ticks: a PeriodicTimer allows one outstanding wait,
            // and a wake that arrives during a tick is held by the signal until the next look.
            Task<bool>? tick = null;
            Task? wake = null;
            while (true)
            {
                await TickAsync(stoppingToken);

                tick ??= timer.WaitForNextTickAsync(stoppingToken).AsTask();
                wake ??= signal.WaitAsync(stoppingToken);
                if (await Task.WhenAny(tick, wake) == tick)
                {
                    if (!await tick)
                    {
                        return;
                    }

                    tick = null;
                }
                else
                {
                    await wake;
                    wake = null;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (!options.Value.IsActive)
        {
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<EmailDispatcher>();
            var result = await dispatcher.RunOnceAsync(timeProvider.GetUtcNow(), ct);
            if (result.Paused)
            {
                logger.LogWarning("SES sending is paused for the account; the dispatcher will retry next tick");
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Email dispatch tick failed");
        }
    }
}
