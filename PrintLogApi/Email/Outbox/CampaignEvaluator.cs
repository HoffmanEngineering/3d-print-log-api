using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrintLogApi.Email.Campaigns;
using PrintLogApi.Extensions;
using PrintLogApi.Models;

namespace PrintLogApi.Email.Outbox;

/// <summary>
/// Turns "who is due" into Pending outbox rows (spec §6.2). Idempotent: the unique key
/// (UserId, Campaign, PeriodKey) means a period is queued at most once however often this runs.
/// </summary>
public sealed class CampaignEvaluator(
    PrintLogContext db,
    IEnumerable<IEmailCampaign> campaigns,
    IOptions<EmailOptions> options,
    TelemetryClient telemetry)
{
    private const int BatchSize = 500;

    /// <summary>Runs between the existence query and the batch insert, so a test can race it.</summary>
    internal Func<CancellationToken, Task>? BeforeInsertForTesting { get; set; }

    /// <returns>Rows inserted.</returns>
    public async Task<int> RunOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        var settings = options.Value;
        if (!settings.IsActive)
        {
            return 0;
        }

        var inserted = 0;
        foreach (var campaign in campaigns)
        {
            if (!settings.IsCampaignEnabled(campaign.Name))
            {
                continue;
            }

            try
            {
                var count = await EvaluateAsync(campaign, now, ct);
                inserted += count;
                if (count > 0)
                {
                    telemetry.TrackEvent(
                        "Email_Queued",
                        new Dictionary<string, string> { ["campaign"] = campaign.Name },
                        new Dictionary<string, double> { ["count"] = count });
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One broken campaign must not stop the others queueing.
                db.ChangeTracker.Clear();
                telemetry.TrackException(ex, new Dictionary<string, string> { ["campaign"] = campaign.Name });
            }
        }

        return inserted;
    }

    private async Task<int> EvaluateAsync(IEmailCampaign campaign, DateTimeOffset now, CancellationToken ct)
    {
        var due = (await campaign.FindDueAsync(now, ct))
            .DistinctBy(d => (d.UserId, d.PeriodKey))
            .ToList();

        var inserted = 0;
        foreach (var batch in due.Chunk(BatchSize))
        {
            inserted += await InsertBatchAsync(campaign, batch, now, ct);
        }

        return inserted;
    }

    private async Task<int> InsertBatchAsync(IEmailCampaign campaign, DueEmail[] batch, DateTimeOffset now, CancellationToken ct)
    {
        var userIds = batch.Select(d => d.UserId).Distinct().ToList();
        var periodKeys = batch.Select(d => d.PeriodKey).Distinct().ToList();

        // Over-matches (any user x any period in the batch) and is filtered exactly in memory,
        // which keeps it one IN-list query that both providers translate.
        var existing = (await db.EmailOutbox
                .Where(o => o.Campaign == campaign.Name && userIds.Contains(o.UserId) && periodKeys.Contains(o.PeriodKey))
                .Select(o => new { o.UserId, o.PeriodKey })
                .ToListAsync(ct))
            .Select(o => (o.UserId, o.PeriodKey))
            .ToHashSet();

        var fresh = batch.Where(d => !existing.Contains((d.UserId, d.PeriodKey))).ToList();
        if (fresh.Count == 0)
        {
            return 0;
        }

        db.EmailOutbox.AddRange(fresh.Select(d => NewRow(campaign, d, now)));
        if (BeforeInsertForTesting is { } hook)
        {
            await hook(ct);
        }

        try
        {
            await db.SaveChangesAsync(ct);
            return fresh.Count;
        }
        catch (DbUpdateException ex) when (UniqueConstraintViolation.IsUniqueViolation(ex))
        {
            // Someone queued one of these between the check and the insert. Start from a clean
            // tracker and insert one at a time, so a failed entity is never retried dirty.
            db.ChangeTracker.Clear();
            var inserted = 0;
            foreach (var d in fresh)
            {
                db.EmailOutbox.Add(NewRow(campaign, d, now));
                try
                {
                    await db.SaveChangesAsync(ct);
                    inserted++;
                }
                catch (DbUpdateException rowEx) when (UniqueConstraintViolation.IsUniqueViolation(rowEx))
                {
                    // Already queued: exactly the outcome we wanted.
                }
                finally
                {
                    db.ChangeTracker.Clear();
                }
            }

            return inserted;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private static EmailOutbox NewRow(IEmailCampaign campaign, DueEmail due, DateTimeOffset now) => new()
    {
        UserId = due.UserId,
        Campaign = campaign.Name,
        PeriodKey = due.PeriodKey,
        Status = EmailOutboxStatus.Pending,
        SendAfter = due.SendAfter,
        NextAttemptAt = due.SendAfter,
        ExpiresAt = due.SendAfter + campaign.Lifetime,
        CreatedAt = now,
    };
}
