using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrintLogApi.Models;

namespace PrintLogApi.Services.SlicerSettings;

/// <summary>
/// Deletes the Cura plugin's and Slic3r uploader's settings records once they are
/// <see cref="CuraSettingsRetentionOptions.RetentionDays"/> old. Such a record exists only to fill
/// the add-print form (<c>GET /api/Cura/settings</c>); the print keeps nothing that points back
/// to it (<c>ApplyProvenance</c> copies the slicer and version, and ignores an id that is gone),
/// so after the form is saved, or abandoned, nothing reads it again. Each one carries the slicer's
/// snapshot image as base64, which is what made the table most of the database.
/// <para>
/// Records with a <see cref="CuraSetting.PrintId"/> are a connector's settings for a print (#164)
/// and are never touched: they are still read, and they hold no image.
/// </para>
/// <para>
/// Batched, oldest first, with a pause between batches: the first run after deploy clears years
/// of backlog, and one large DELETE would hold the S0 database's log allowance for hours. The
/// first tick waits five minutes so a cold start is not slowed. A failed tick is logged and the
/// loop carries on; the next one resumes where it stopped.
/// </para>
/// </summary>
public sealed class CuraSettingsRetentionJob(
    IServiceScopeFactory scopeFactory,
    IOptions<CuraSettingsRetentionOptions> options,
    TimeProvider timeProvider,
    ILogger<CuraSettingsRetentionJob> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        try
        {
            await Task.Delay(InitialDelay, timeProvider, stoppingToken);
            using var timer = new PeriodicTimer(options.Value.RunInterval, timeProvider);
            do
            {
                try
                {
                    await RunOnceAsync(timeProvider.GetUtcNow(), stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Slicer settings retention failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Deletes every expired uploader record, batch by batch, and returns how many.</summary>
    internal async Task<int> RunOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        var settings = options.Value;
        var cutoff = now.AddDays(-settings.RetentionDays);
        var total = 0;

        while (true)
        {
            int deleted;
            // A scope per batch, so a long backlog run never holds one context (or its
            // connection) for hours.
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                deleted = await Expired(context, cutoff, settings.BatchSize).ExecuteDeleteAsync(ct);
            }

            total += deleted;
            if (deleted < settings.BatchSize)
            {
                break;
            }

            if (settings.BatchPause > TimeSpan.Zero)
            {
                await Task.Delay(settings.BatchPause, timeProvider, ct);
            }
        }

        if (total > 0)
        {
            logger.LogInformation("Deleted {Count} slicer uploader settings records older than {Cutoff:O}", total, cutoff);
        }

        return total;
    }

    /// <summary>
    /// One batch: unlinked records created before <paramref name="cutoff"/>, oldest first. The
    /// literal <c>PrintId IS NULL</c> matches the filter of <c>IX_CuraSettings_Unlinked_CreatedDate</c>,
    /// so the batch is a range seek on that index instead of a scan of the table. Internal for the
    /// SQL Server translation test.
    /// </summary>
    internal static IQueryable<CuraSetting> Expired(PrintLogContext context, DateTimeOffset cutoff, int batchSize) =>
        context.CuraSettings
            .Where(c => c.PrintId == null && c.CreatedDate < cutoff)
            .OrderBy(c => c.CreatedDate)
            .Take(batchSize);
}
