using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrintLogApi.SpoolTags.Catalog;

namespace PrintLogApi.SpoolTags;

/// <summary>
/// Once a day: imports the SpoolmanDB catalog (when enabled) and deletes failed tag reads past
/// their retention. The first tick waits five minutes so a cold start is not slowed. A failed tick
/// is logged and the loop carries on; only shutdown ends it.
/// </summary>
public sealed class SpoolTagMaintenanceService(
    IServiceScopeFactory scopeFactory,
    IOptions<SpoolTagOptions> options,
    TimeProvider timeProvider,
    ILogger<SpoolTagMaintenanceService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, timeProvider, stoppingToken);
            using var timer = new PeriodicTimer(options.Value.RunInterval, timeProvider);
            do
            {
                try
                {
                    await RunOnceAsync(timeProvider.GetUtcNow().UtcDateTime, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Spool tag maintenance failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task RunOnceAsync(DateTime utcNow, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        if (options.Value.CatalogImportEnabled)
        {
            try
            {
                var result = await scope.ServiceProvider.GetRequiredService<FilamentCatalogImporter>().ImportAsync(ct);
                logger.LogInformation(
                    "Filament catalog import: {Added} added, {Updated} updated, {Removed} removed, not modified {NotModified}",
                    result.Added, result.Updated, result.Removed, result.NotModified);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // A catalog outage must not keep old failed reads from being purged.
                logger.LogError(ex, "Filament catalog import failed");
            }
        }

        var cutoff = utcNow.AddDays(-options.Value.FailureRetentionDays);
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var purged = await context.TagReadFailures.Where(f => f.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
        if (purged > 0)
        {
            logger.LogInformation("Purged {Count} failed tag reads older than {Cutoff:O}", purged, cutoff);
        }
    }
}
