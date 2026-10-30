using System.Globalization;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using Prometheus;

namespace PrintLogApi.Services.DeviceGrants;

public interface IDevicePairingNotifier
{
    /// <summary>
    /// The in-app half of the pairing notice (api#287): a notification, with its push, for a
    /// pairing that committed and queued the email (<see cref="PairedDevice.Noticed"/>). It
    /// never throws.
    /// </summary>
    Task NotifyInAppAsync(DeviceGrant grant, bool repaired, string? clientIp);
}

/// <summary>
/// The pairing notice's in-app notification. The email is not sent from here: it is queued in
/// the pairing's own save (<see cref="DeviceGrantService"/>), so it cannot be lost and a pairing
/// whose email cannot be queued fails. The notification is the best-effort extra, written after
/// the commit; a failure is logged and counted (<c>DevicePairingNoticeFailed</c>,
/// <c>printlog_device_pairing_notice_failures_total</c>) but never fails the pairing, which
/// already committed. Neither carries the client address to telemetry.
/// </summary>
public sealed class DevicePairingNotifier(
    PrintLogContext context,
    INotificationService notifications,
    TelemetryClient telemetry,
    ILogger<DevicePairingNotifier> logger) : IDevicePairingNotifier
{
    private static readonly Counter Failures = Metrics.CreateCounter(
        "printlog_device_pairing_notice_failures_total", "Device pairing notices that could not be written, by channel.", "channel");

    public async Task NotifyInAppAsync(DeviceGrant grant, bool repaired, string? clientIp)
    {
        try
        {
            await notifications.CreateDevicePairedNotification(grant.UserId, grant.Name, repaired, clientIp);
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
        logger.LogWarning(ex, "Could not write the {Channel} pairing notice for device grant {GrantId}", channel, grantId);
        Failures.WithLabels(channel).Inc();
        telemetry.TrackEvent("DevicePairingNoticeFailed", new Dictionary<string, string>
        {
            ["GrantId"] = grantId.ToString(CultureInfo.InvariantCulture),
            ["Channel"] = channel,
        });
    }
}
