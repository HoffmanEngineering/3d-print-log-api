using Microsoft.Extensions.Options;

namespace PrintLogApi.Email.Outbox;

/// <summary>
/// Runs <see cref="CampaignEvaluator"/> every <see cref="EmailOptions.EvaluatorInterval"/>. The
/// first tick waits a minute so a cold start is not slowed by campaign queries. A failed tick is
/// logged and the loop carries on; only shutdown ends it.
/// </summary>
public sealed class CampaignEvaluatorService(
    IServiceScopeFactory scopeFactory,
    IOptions<EmailOptions> options,
    TimeProvider timeProvider,
    ILogger<CampaignEvaluatorService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, timeProvider, stoppingToken);
            using var timer = new PeriodicTimer(options.Value.EvaluatorInterval, timeProvider);
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
            var evaluator = scope.ServiceProvider.GetRequiredService<CampaignEvaluator>();
            await evaluator.RunOnceAsync(timeProvider.GetUtcNow(), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Email campaign evaluation failed");
        }
    }
}
