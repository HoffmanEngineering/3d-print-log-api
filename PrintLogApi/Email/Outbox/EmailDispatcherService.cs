using Microsoft.Extensions.Options;

namespace PrintLogApi.Email.Outbox;

/// <summary>
/// Runs <see cref="EmailDispatcher"/> every <see cref="EmailOptions.DispatchInterval"/>. Single
/// instance only (spec §6.12): the daily cap and send-rate limit are per process.
/// </summary>
public sealed class EmailDispatcherService(
    IServiceScopeFactory scopeFactory,
    IOptions<EmailOptions> options,
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
            do
            {
                await TickAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
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
