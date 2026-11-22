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
/// Batched, oldest first, with a pause between batches: one large DELETE would hold the S0
/// database's log allowance for hours. A run stops at
/// <see cref="CuraSettingsRetentionOptions.MaxRunDuration"/> or at the first failed batch, and
/// either way the next run comes after
/// <see cref="CuraSettingsRetentionOptions.BacklogRetryInterval"/> (an hour) instead of a day, so
/// a backlog (554,000 rows when this shipped) is cleared in hourly slices and a timeout costs an
/// hour, not a day. The first run waits ten minutes so a cold start is not slowed, and so it does
/// not start together with <c>PrinterPopularityJob</c> (five minutes): on S0 the two timed out
/// together.
/// </para>
/// </summary>
public sealed class CuraSettingsRetentionJob(
    IServiceScopeFactory scopeFactory,
    IOptions<CuraSettingsRetentionOptions> options,
    TimeProvider timeProvider,
    ILogger<CuraSettingsRetentionJob> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return;
        }

        try
        {
            await Task.Delay(InitialDelay, timeProvider, stoppingToken);
            while (true)
            {
                var result = await RunOnceAsync(timeProvider.GetUtcNow(), stoppingToken);
                await Task.Delay(NextRunDelay(result, settings), timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>How long to wait after a run: an hour while expired records are left, else a day.</summary>
    internal static TimeSpan NextRunDelay(RetentionRunResult result, CuraSettingsRetentionOptions settings) =>
        result.BacklogRemains ? settings.BacklogRetryInterval : settings.RunInterval;

    /// <summary>
    /// Deletes expired uploader records, batch by batch, until none is left, the run reaches
    /// <see cref="CuraSettingsRetentionOptions.MaxRunDuration"/>, or a batch fails. A failure is
    /// logged here and never thrown (cancellation is), so the caller only has to schedule the next run.
    /// </summary>
    internal async Task<RetentionRunResult> RunOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        var settings = options.Value;
        var cutoff = now.AddDays(-settings.RetentionDays);
        var started = timeProvider.GetTimestamp();
        var total = 0;
        var backlogRemains = false;

        try
        {
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

                if (timeProvider.GetElapsedTime(started) >= settings.MaxRunDuration)
                {
                    backlogRemains = true;
                    break;
                }

                if (settings.BatchPause > TimeSpan.Zero)
                {
                    await Task.Delay(settings.BatchPause, timeProvider, ct);
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex,
                "Slicer settings retention failed after deleting {Count} records older than {Cutoff:O}; retrying in {Retry}",
                total, cutoff, settings.BacklogRetryInterval);
            return new RetentionRunResult(total, BacklogRemains: true);
        }

        if (total > 0)
        {
            logger.LogInformation(
                "Deleted {Count} slicer uploader settings records older than {Cutoff:O}; backlog remains: {BacklogRemains}",
                total, cutoff, backlogRemains);
        }

        return new RetentionRunResult(total, backlogRemains);
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

/// <summary>What one retention run did: how many records it deleted, and whether expired ones are left.</summary>
internal readonly record struct RetentionRunResult(int Deleted, bool BacklogRemains);
