using System.Globalization;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Email.Campaigns;
using PrintLogApi.Models;
using Prometheus;

namespace PrintLogApi.Services.DeviceGrants;

public interface IDevicePairingNotifier
{
    /// <summary>
    /// Tells the owner a device paired (api#287): a security email, queued in the outbox, and an
    /// in-app notification. Call it after the pairing committed. It never throws.
    /// </summary>
    Task NotifyAsync(DeviceGrant grant, bool created);
}

/// <summary>
/// The pairing notice. It runs after the grant's save, never inside it: the email is queued and
/// the notification (with its push) written only once the pairing is real, and a failure here is
/// logged and counted (<c>DevicePairingNoticeFailed</c>, <c>printlog_device_pairing_notice_failures_total</c>)
/// but never fails the pairing, which already committed.
///
/// <para>
/// At most one notice per grant per <see cref="RepeatAfter"/>, claimed with one conditional
/// update of <see cref="DeviceGrant.PairingNoticeAt"/>, so concurrent re-pairs of one device and
/// a bridge that re-pairs in a loop send one, on any instance. A new device is a new grant and
/// always gets one. A retry of the same pairing calls this too and is normally inside the hour,
/// so a client that hangs up after the commit cannot skip the notice by retrying.
/// </para>
/// </summary>
public sealed class DevicePairingNotifier(
    PrintLogContext context,
    INotificationService notifications,
    TimeProvider clock,
    TelemetryClient telemetry,
    ILogger<DevicePairingNotifier> logger) : IDevicePairingNotifier
{
    /// <summary>The per-device throttle: re-pairs within it after a notice are not noticed again.</summary>
    public static readonly TimeSpan RepeatAfter = TimeSpan.FromHours(1);

    private static readonly Counter Failures = Metrics.CreateCounter(
        "printlog_device_pairing_notice_failures_total", "Device pairing notices that could not be queued or written, by channel.", "channel");

    public async Task NotifyAsync(DeviceGrant grant, bool created)
    {
        // Not the request's token: the pairing committed, and a client that disconnects right
        // after must not be able to cancel the notice of it.
        var ct = CancellationToken.None;
        var at = clock.GetUtcNow();
        var now = at.UtcDateTime;
        var cutoff = now - RepeatAfter;

        int claimed;
        try
        {
            claimed = await context.DeviceGrants
                .Where(g => g.Id == grant.Id && (g.PairingNoticeAt == null || g.PairingNoticeAt <= cutoff))
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.PairingNoticeAt, now), ct);
        }
        catch (Exception ex)
        {
            Failed("claim", grant.Id, ex);
            return;
        }

        if (claimed == 0)
        {
            // Noticed within the hour, or revoked since the pairing: nothing to tell.
            telemetry.TrackEvent("DevicePairingNoticeThrottled", Properties(grant.Id));
            return;
        }

        var repaired = !created;
        try
        {
            context.EmailOutbox.Add(new EmailOutbox
            {
                UserId = grant.UserId,
                Campaign = DevicePairedCampaign.CampaignName,
                // Unique per claim, and claims are an hour apart.
                PeriodKey = $"grant:{grant.Id.ToString(CultureInfo.InvariantCulture)}:{now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}",
                Status = EmailOutboxStatus.Pending,
                SendAfter = at,
                NextAttemptAt = at,
                ExpiresAt = at + TimeSpan.FromDays(3),
                CreatedAt = at,
                Exposure = DevicePairedCampaign.Snapshot(grant.Id, grant.Name, grant.Scopes, now, repaired).ToJsonString(),
            });
            await context.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            DropUnsaved();
            Failed("email", grant.Id, ex);
        }

        try
        {
            await notifications.CreateDevicePairedNotification(grant.UserId, grant.Name, repaired);
        }
        catch (Exception ex)
        {
            DropUnsaved();
            Failed("in_app", grant.Id, ex);
        }
    }

    /// <summary>A failed insert stays tracked and would be retried by the next save on this context.</summary>
    private void DropUnsaved()
    {
        foreach (var entry in context.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private void Failed(string channel, long grantId, Exception ex)
    {
        logger.LogWarning(ex, "Could not send the {Channel} pairing notice for device grant {GrantId}", channel, grantId);
        Failures.WithLabels(channel).Inc();
        var properties = Properties(grantId);
        properties["Channel"] = channel;
        telemetry.TrackEvent("DevicePairingNoticeFailed", properties);
    }

    private static Dictionary<string, string> Properties(long grantId) => new()
    {
        ["GrantId"] = grantId.ToString(CultureInfo.InvariantCulture),
    };
}
