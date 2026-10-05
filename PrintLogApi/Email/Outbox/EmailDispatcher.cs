using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrintLogApi.Email.Campaigns;
using PrintLogApi.Email.Templates;
using PrintLogApi.Email.Tokens;
using PrintLogApi.Email.Transport;
using PrintLogApi.Models;

namespace PrintLogApi.Email.Outbox;

public sealed record DispatchTickResult(int Claimed, int Sent, int Skipped, int Deferred, int Failed, bool Paused);

/// <summary>
/// One pass over the outbox (spec §6.3): reap abandoned claims, expire stale rows, then claim due
/// rows (at most one per user) and push each through the gates in order until it is sent,
/// skipped, deferred or failed.
///
/// Delivery is at most once. SES SendEmail has no idempotency key, so any failure after the
/// request may have reached SES is marked failed rather than retried.
/// </summary>
public sealed class EmailDispatcher(
    PrintLogContext db,
    IEnumerable<IEmailCampaign> campaigns,
    IEmailPreferenceService preferences,
    IEmailAddressHasher hasher,
    IEmailTransport transport,
    IEmailTokenService tokens,
    EmailLinkBuilder links,
    IOptions<EmailOptions> options,
    TimeProvider clock,
    TelemetryClient telemetry)
{
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2)];

    private const int MaxAttempts = 5;
    private const int MaxErrorLength = 512;
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan GateRecheck = TimeSpan.FromHours(1);

    private readonly Dictionary<string, IEmailCampaign> _campaigns = campaigns.ToDictionary(c => c.Name, StringComparer.Ordinal);

    /// <summary>Runs after candidates are chosen and before they are claimed, so a test can race a second dispatcher.</summary>
    internal Func<CancellationToken, Task>? BeforeClaimForTesting { get; set; }

    private enum Outcome { Sent, Skipped, Deferred, Failed, Paused }

    public async Task<DispatchTickResult> RunOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        var settings = options.Value;
        if (!settings.IsActive)
        {
            return new DispatchTickResult(0, 0, 0, 0, 0, false);
        }

        await ReapAbandonedClaimsAsync(now, ct);
        await ExpireAsync(now, ct);

        var batchSize = settings.BatchSize;
        if (settings.DailyCap is { } cap)
        {
            var startOfUtcDay = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            var sentToday = await db.EmailOutbox.CountAsync(o => o.Status == EmailOutboxStatus.Sent && o.SentAt >= startOfUtcDay, ct);
            batchSize = Math.Min(batchSize, cap - sentToday);
        }

        int claimed = 0, sent = 0, skipped = 0, deferred = 0, failed = 0;
        var paused = false;

        if (batchSize > 0)
        {
            // Over-fetch, then keep each user's earliest row: a user's second row waits for the
            // next tick, where the frequency cap sees the first as Sent.
            var candidates = (await db.EmailOutbox
                    .AsNoTracking()
                    .Where(o => o.Status == EmailOutboxStatus.Pending && o.NextAttemptAt <= now)
                    .OrderBy(o => o.NextAttemptAt).ThenBy(o => o.Id)
                    .Select(o => new { o.Id, o.UserId })
                    .Take(batchSize * 3)
                    .ToListAsync(ct))
                .DistinctBy(o => o.UserId)
                .Take(batchSize)
                .Select(o => o.Id)
                .ToList();

            if (BeforeClaimForTesting is { } hook)
            {
                await hook(ct);
            }

            var spacing = TimeSpan.FromMilliseconds(1000.0 / settings.MaxSendsPerSecond);
            foreach (var id in candidates)
            {
                if (!await TryClaimAsync(id, now, ct))
                {
                    continue;
                }

                claimed++;
                var outcome = await ProcessAsync(id, now, settings, ct);
                db.ChangeTracker.Clear();

                switch (outcome)
                {
                    case Outcome.Sent:
                        sent++;
                        await Task.Delay(spacing, clock, ct);
                        break;
                    case Outcome.Skipped:
                        skipped++;
                        break;
                    case Outcome.Deferred:
                        deferred++;
                        break;
                    case Outcome.Failed:
                        failed++;
                        break;
                    case Outcome.Paused:
                        paused = true;
                        break;
                }

                if (paused)
                {
                    break;
                }
            }
        }

        EmailMetrics.OutboxPending.Set(await db.EmailOutbox.CountAsync(o => o.Status == EmailOutboxStatus.Pending, ct));
        return new DispatchTickResult(claimed, sent, skipped, deferred, failed, paused);
    }

    private async Task ReapAbandonedClaimsAsync(DateTimeOffset now, CancellationToken ct)
    {
        // The process died between claim and outcome. It may have sent, so this is ambiguous,
        // never a retry. A later Delivery event for the row's outbox_id tag flips it to Sent.
        var cutoff = now - AbandonedAfter;
        await db.EmailOutbox
            .Where(o => o.Status == EmailOutboxStatus.Sending && o.ClaimedAt < cutoff)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, EmailOutboxStatus.Failed)
                .SetProperty(o => o.LastError, "ambiguous: claim abandoned"), ct);
    }

    private async Task ExpireAsync(DateTimeOffset now, CancellationToken ct)
        => await db.EmailOutbox
            .Where(o => o.Status == EmailOutboxStatus.Pending && o.ExpiresAt < now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, EmailOutboxStatus.Skipped)
                .SetProperty(o => o.SkipReason, EmailSkipReasons.Expired), ct);

    /// <summary>Atomic on both providers: exactly one caller sees one affected row.</summary>
    private async Task<bool> TryClaimAsync(long id, DateTimeOffset now, CancellationToken ct)
        => await db.EmailOutbox
            .Where(o => o.Id == id && o.Status == EmailOutboxStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, EmailOutboxStatus.Sending)
                .SetProperty(o => o.ClaimedAt, now), ct) == 1;

    private async Task<Outcome> ProcessAsync(long id, DateTimeOffset now, EmailOptions settings, CancellationToken ct)
    {
        var row = await db.EmailOutbox.SingleAsync(o => o.Id == id, ct);
        EmailMessage message;

        try
        {
            var prepared = await PrepareAsync(row, now, settings, ct);
            if (prepared.Outcome is { } decided)
            {
                await db.SaveChangesAsync(ct);
                return decided;
            }

            message = prepared.Message!;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Nothing has been handed to the transport yet, so this is safe to retry.
            telemetry.TrackException(ex, new Dictionary<string, string> { ["campaign"] = row.Campaign, ["outboxId"] = id.ToString() });
            db.ChangeTracker.Clear();
            row = await db.EmailOutbox.SingleAsync(o => o.Id == id, ct);
            var outcome = Retry(row, now, $"error: {ex.Message}");
            await db.SaveChangesAsync(ct);
            return outcome;
        }

        return await SendAsync(row, message, now, ct);
    }

    private sealed record Prepared(Outcome? Outcome, EmailMessage? Message);

    /// <summary>Gates 1–12. Mutates the row for every outcome except "go ahead and send".</summary>
    private async Task<Prepared> PrepareAsync(EmailOutbox row, DateTimeOffset now, EmailOptions settings, CancellationToken ct)
    {
        var user = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == row.UserId)
            .Select(u => new { u.Email, u.EmailVerified, u.DeactivationDateTime })
            .SingleOrDefaultAsync(ct);

        if (user is null)
        {
            return Skip(row, EmailSkipReasons.UserMissing);
        }

        if (user.DeactivationDateTime is not null)
        {
            return Skip(row, EmailSkipReasons.Deactivating);
        }

        if (!_campaigns.TryGetValue(row.Campaign, out var campaign) || !settings.IsCampaignEnabled(row.Campaign))
        {
            return Skip(row, EmailSkipReasons.DisabledCampaign);
        }

        if (string.IsNullOrWhiteSpace(user.Email) || !user.EmailVerified)
        {
            return Skip(row, EmailSkipReasons.Unverified);
        }

        var hashes = hasher.CandidateHashes(user.Email);
        if (await db.EmailSuppressions.AnyAsync(s => hashes.Contains(s.EmailHash), ct))
        {
            return Skip(row, EmailSkipReasons.Suppressed);
        }

        if (!await preferences.IsAllowedAsync(row.UserId, campaign.PreferenceSettingTypeId, ct))
        {
            return Skip(row, EmailSkipReasons.OptedOut);
        }

        if (settings.Notice.Required && !await preferences.HasSeenNoticeAsync(row.UserId, ct))
        {
            return Defer(row, now + GateRecheck, "notice");
        }

        if (settings.AllowListUserIds.Length > 0 && !settings.AllowListUserIds.Contains(row.UserId))
        {
            return Defer(row, now + GateRecheck, "allow-list");
        }

        if (!campaign.ExemptFromFrequencyCap)
        {
            var counting = _campaigns.Values.Where(c => c.CountsTowardFrequencyCap).Select(c => c.Name).ToList();
            var windowStart = now - settings.FrequencyCapWindow;
            var lastSent = await db.EmailOutbox
                .Where(o => o.UserId == row.UserId && o.Status == EmailOutboxStatus.Sent
                    && counting.Contains(o.Campaign) && o.SentAt > windowStart)
                .OrderByDescending(o => o.SentAt)
                .Select(o => o.SentAt)
                .FirstOrDefaultAsync(ct);

            if (lastSent is { } last)
            {
                return Defer(row, last + settings.FrequencyCapWindow, "frequency-cap");
            }

            // One dispatcher only claims one row per user per tick, but two can overlap (a slot
            // swap, a second instance), and the other's row is Sending, not yet Sent. Look again
            // once it has settled; the reaper bounds how long that can take.
            var claimedCutoff = now - AbandonedAfter;
            var inFlight = await db.EmailOutbox.AnyAsync(o => o.UserId == row.UserId && o.Id != row.Id
                && o.Status == EmailOutboxStatus.Sending && counting.Contains(o.Campaign) && o.ClaimedAt >= claimedCutoff, ct);
            if (inFlight)
            {
                return Defer(row, now + TimeSpan.FromMinutes(1), "frequency-cap");
            }
        }

        var rendered = await campaign.RenderAsync(row, ct);
        if (rendered is null)
        {
            return Skip(row, EmailSkipReasons.NotRelevant);
        }

        // Written for both arms, after relevance, so holdout and treatment rows are comparable.
        var holdout = EmailHoldout.IsHoldout(row.UserId, settings.HoldoutPercent);
        var exposure = rendered.Exposure.DeepClone().AsObject();
        exposure["eligibleAt"] = now;
        exposure["arm"] = holdout ? "holdout" : "treatment";
        row.Exposure = exposure.ToJsonString();

        if (holdout)
        {
            return Skip(row, EmailSkipReasons.Holdout);
        }

        if (settings.DryRun)
        {
            telemetry.TrackEvent("Email_DryRun", new Dictionary<string, string> { ["campaign"] = row.Campaign });
            return Skip(row, EmailSkipReasons.DryRun);
        }

        var unsubscribe = links.OneClick(tokens.CreateUnsubscribe(row.UserId, rendered.UnsubscribeCategory));
        var headers = new Dictionary<string, string>
        {
            ["List-Unsubscribe"] = $"<{unsubscribe}>",
            ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click",
        };

        return new Prepared(null, new EmailMessage(user.Email, rendered.Subject, rendered.Html, rendered.Text, headers, row.Campaign, row.Id));
    }

    private async Task<Outcome> SendAsync(EmailOutbox row, EmailMessage message, DateTimeOffset now, CancellationToken ct)
    {
        Outcome outcome;
        try
        {
            var messageId = await transport.SendAsync(message, ct);
            row.Status = EmailOutboxStatus.Sent;
            row.SentAt = now;
            row.ProviderMessageId = messageId;
            row.SentTo = hasher.Hash(message.ToAddress);
            row.Attempts++;
            row.LastError = null;
            telemetry.TrackEvent("Email_Sent", new Dictionary<string, string> { ["campaign"] = row.Campaign });
            EmailMetrics.Sent.WithLabels(row.Campaign).Inc();
            outcome = Outcome.Sent;
        }
        catch (Exception ex)
        {
            var kind = ex is EmailSendException send ? send.Kind : EmailSendFailureKind.Ambiguous;
            outcome = kind switch
            {
                EmailSendFailureKind.Throttled or EmailSendFailureKind.NotConnected => Retry(row, now, $"{kind}: {ex.Message}"),
                EmailSendFailureKind.Rejected => Fail(row, $"rejected: {ex.Message}", kind),
                EmailSendFailureKind.AccountPaused => Pause(row),
                _ => Fail(row, $"ambiguous: {ex.Message}", kind),
            };
        }

        // If this save fails after a successful send, the row stays Sending and is reaped as
        // ambiguous: the right reading, since the message did go out.
        await db.SaveChangesAsync(ct);
        return outcome;
    }

    private Prepared Skip(EmailOutbox row, string reason)
    {
        row.Status = EmailOutboxStatus.Skipped;
        row.SkipReason = reason;
        row.ClaimedAt = null;
        telemetry.TrackEvent("Email_Skipped", new Dictionary<string, string> { ["campaign"] = row.Campaign, ["reason"] = reason });
        EmailMetrics.Skipped.WithLabels(row.Campaign, reason).Inc();
        return new Prepared(Outcome.Skipped, null);
    }

    private Prepared Defer(EmailOutbox row, DateTimeOffset until, string why)
    {
        row.Status = EmailOutboxStatus.Pending;
        row.NextAttemptAt = until;
        row.ClaimedAt = null;
        telemetry.TrackEvent("Email_Deferred", new Dictionary<string, string> { ["campaign"] = row.Campaign, ["why"] = why });
        return new Prepared(Outcome.Deferred, null);
    }

    /// <summary>Definitely not sent: back off, or give up after the fifth attempt.</summary>
    private Outcome Retry(EmailOutbox row, DateTimeOffset now, string error)
    {
        row.Attempts++;
        row.ClaimedAt = null;
        if (row.Attempts >= MaxAttempts)
        {
            return Fail(row, error, EmailSendFailureKind.Throttled);
        }

        row.Status = EmailOutboxStatus.Pending;
        row.NextAttemptAt = now + Backoff[row.Attempts - 1];
        row.LastError = Truncate(error);
        return Outcome.Deferred;
    }

    private Outcome Fail(EmailOutbox row, string error, EmailSendFailureKind kind)
    {
        row.Status = EmailOutboxStatus.Failed;
        row.LastError = Truncate(error);
        telemetry.TrackEvent("Email_Failed", new Dictionary<string, string> { ["campaign"] = row.Campaign, ["kind"] = kind.ToString() });
        EmailMetrics.Failed.WithLabels(row.Campaign, kind.ToString()).Inc();
        return Outcome.Failed;
    }

    /// <summary>The account, not the row, is at fault: put it back untouched and stop the tick.</summary>
    private Outcome Pause(EmailOutbox row)
    {
        row.Status = EmailOutboxStatus.Pending;
        row.ClaimedAt = null;
        telemetry.TrackEvent("Email_SendingPaused");
        return Outcome.Paused;
    }

    private static string Truncate(string value) => value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];
}
