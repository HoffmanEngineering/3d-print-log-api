using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Authentication.Devices;
using PrintLogApi.Email.Templates;
using PrintLogApi.Models;
using PrintLogApi.Services;

namespace PrintLogApi.Email.Campaigns;

/// <summary>
/// "A device was paired with your account" (api#287, item 1): a security notice, so a phished
/// approval (someone sends the user a <c>verification_uri_complete</c> link) does not produce a
/// device token nobody notices.
///
/// Nothing here finds who is due: <c>DevicePairingNotifier</c> queues a row after the pairing
/// commits, with a snapshot of the grant in <see cref="EmailOutbox.Exposure"/> (name, scopes,
/// time), because the grant can be renamed, re-paired or revoked before the dispatcher gets to
/// it and the email must describe the pairing that happened.
/// </summary>
public sealed class DevicePairedCampaign(
    PrintLogContext db,
    IEmailTemplateRenderer renderer,
    IEmailFooterFactory footers,
    EmailLinkBuilder links) : IEmailCampaign
{
    public const string CampaignName = "device-paired";

    public const string FooterReason =
        "You're receiving this security notice because a device was paired with your 3D Print Log account. Security notices are sent whatever your email preferences.";

    /// <summary>Settings, scrolled to Connected Agents, where paired devices are revoked.</summary>
    public const string ReviewPath = "/settings#connected-agents";

    public string Name => CampaignName;

    /// <summary>Never consulted: a security notice ignores preferences.</summary>
    public int PreferenceSettingTypeId => EmailSettingTypes.All;

    public bool CountsTowardFrequencyCap => false;

    public bool ExemptFromFrequencyCap => true;

    public bool IsSecurityNotice => true;

    /// <summary>Long enough to outlast a day at the daily cap, which it still counts toward.</summary>
    public TimeSpan Lifetime => TimeSpan.FromDays(3);

    public Task<IReadOnlyList<DueEmail>> FindDueAsync(DateTimeOffset now, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<DueEmail>>([]);

    /// <summary>What the notifier writes into <see cref="EmailOutbox.Exposure"/> when it queues the row.</summary>
    public static JsonObject Snapshot(long grantId, string deviceName, string scopes, DateTime pairedAtUtc, bool repaired) => new()
    {
        ["grantId"] = grantId,
        ["deviceName"] = deviceName,
        ["scopes"] = scopes,
        ["pairedAt"] = DateTime.SpecifyKind(pairedAtUtc, DateTimeKind.Utc),
        ["repaired"] = repaired,
    };

    public async Task<RenderedEmail?> RenderAsync(EmailOutbox row, CancellationToken ct)
    {
        if (row.Exposure is null || JsonNode.Parse(row.Exposure) is not JsonObject snapshot
            || snapshot["deviceName"]?.GetValue<string>() is not { } deviceName
            || snapshot["scopes"]?.GetValue<string>() is not { } scopes
            || snapshot["pairedAt"]?.GetValue<DateTime>() is not { } pairedAt)
        {
            return null;
        }

        var user = await db.Users.AsNoTracking().Where(u => u.Id == row.UserId).Select(u => new { u.DisplayName }).SingleOrDefaultAsync(ct);
        if (user is null)
        {
            return null;
        }

        var model = new DevicePairedModel(
            user.DisplayName,
            deviceName,
            snapshot["repaired"]?.GetValue<bool>() ?? false,
            When(pairedAt.ToUniversalTime(), await EmailUserZones.LoadAsync(db, row.UserId, ct)),
            DeviceScopes.Describe(DeviceScopes.Split(scopes)),
            links.Web(ReviewPath, CampaignName, "notice"));

        var footer = footers.SecurityNotice(row.UserId, FooterReason);
        var (html, text) = await DevicePairedTemplates.RenderAsync(renderer, model, footer);

        return new RenderedEmail(DevicePairedTemplates.Subject(model), html, text, PreferenceSettingTypeId, (JsonObject)snapshot.DeepClone());
    }

    /// <summary>
    /// "October 30, 2026 at 3:04 PM (America/Chicago)" in the zone the user saved, else
    /// "October 30, 2026 at 8:04 PM UTC". Never the server's or the configured default zone: a
    /// time in a zone the user didn't pick, unlabeled, would mislead in a security notice.
    /// </summary>
    internal static string When(DateTime utc, string? userZoneId)
    {
        const string format = "MMMM d, yyyy 'at' h:mm tt";
        return TimeZoneResolver.TryResolve(userZoneId, out var zone) && zone.Id != TimeZoneInfo.Utc.Id
            ? $"{TimeZoneInfo.ConvertTimeFromUtc(utc, zone).ToString(format, CultureInfo.InvariantCulture)} ({userZoneId!.Trim()})"
            : $"{utc.ToString(format, CultureInfo.InvariantCulture)} UTC";
    }
}
