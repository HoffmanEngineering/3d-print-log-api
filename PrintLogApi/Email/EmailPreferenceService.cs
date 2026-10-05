using System.Globalization;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Extensions;
using PrintLogApi.Models;

namespace PrintLogApi.Email;

public sealed record EmailPreferenceSnapshot(
    bool All,
    bool Onboarding,
    bool MonthlyRecap,
    bool PrinterSilent,
    DateTimeOffset? NoticeSeenAt);

public interface IEmailPreferenceService
{
    Task<EmailPreferenceSnapshot> GetAsync(long userId, CancellationToken ct);

    /// <summary>Upserts one preference. Safe against a concurrent first write for the same setting.</summary>
    Task SetAsync(long userId, int settingTypeId, bool enabled, CancellationToken ct);

    /// <summary>Master switch AND the campaign's own switch.</summary>
    Task<bool> IsAllowedAsync(long userId, int campaignSettingTypeId, CancellationToken ct);

    Task<bool> HasSeenNoticeAsync(long userId, CancellationToken ct);
}

public sealed class EmailPreferenceService(PrintLogContext db, TelemetryClient telemetry) : IEmailPreferenceService
{
    private static readonly int[] PreferenceTypes =
    [
        EmailSettingTypes.All,
        EmailSettingTypes.Onboarding,
        EmailSettingTypes.MonthlyRecap,
        EmailSettingTypes.PrinterSilent,
        EmailSettingTypes.NoticeSeenAt,
    ];

    public async Task<EmailPreferenceSnapshot> GetAsync(long userId, CancellationToken ct)
    {
        var values = await LoadAsync(userId, PreferenceTypes, ct);

        return new EmailPreferenceSnapshot(
            Read(values, EmailSettingTypes.All),
            Read(values, EmailSettingTypes.Onboarding),
            Read(values, EmailSettingTypes.MonthlyRecap),
            Read(values, EmailSettingTypes.PrinterSilent),
            ParseTimestamp(values.GetValueOrDefault(EmailSettingTypes.NoticeSeenAt)));
    }

    public async Task<bool> IsAllowedAsync(long userId, int campaignSettingTypeId, CancellationToken ct)
    {
        var values = await LoadAsync(userId, [EmailSettingTypes.All, campaignSettingTypeId], ct);
        return Read(values, EmailSettingTypes.All) && Read(values, campaignSettingTypeId);
    }

    public async Task<bool> HasSeenNoticeAsync(long userId, CancellationToken ct)
    {
        var values = await LoadAsync(userId, [EmailSettingTypes.NoticeSeenAt], ct);
        return ParseTimestamp(values.GetValueOrDefault(EmailSettingTypes.NoticeSeenAt)) is not null;
    }

    public async Task SetAsync(long userId, int settingTypeId, bool enabled, CancellationToken ct)
    {
        var stored = EmailPreference.ToStored(enabled);

        if (await TryUpdateAsync(userId, settingTypeId, stored, ct))
        {
            return;
        }

        db.UserSettings.Add(new UserSetting
        {
            UserId = userId,
            UserSettingTypeId = settingTypeId,
            Value = stored,
            CreatedById = userId,
            UpdatedById = userId,
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (UniqueConstraintViolation.IsUniqueViolation(ex))
        {
            // A concurrent first write won the insert. Drop the failed entity from the tracker
            // (retrying it would fail the same way) and update the row the winner created.
            db.ChangeTracker.Clear();
            await TryUpdateAsync(userId, settingTypeId, stored, ct);
        }
    }

    private async Task<bool> TryUpdateAsync(long userId, int settingTypeId, string stored, CancellationToken ct)
    {
        var existing = await db.UserSettings
            .SingleOrDefaultAsync(s => s.UserId == userId && s.UserSettingTypeId == settingTypeId, ct);
        if (existing is null)
        {
            return false;
        }

        if (existing.Value != stored)
        {
            existing.Value = stored;
            existing.UpdatedById = userId;
            await db.SaveChangesAsync(ct);
        }

        return true;
    }

    private async Task<Dictionary<int, string?>> LoadAsync(long userId, int[] types, CancellationToken ct)
        => await db.UserSettings
            .AsNoTracking()
            .Where(s => s.UserId == userId && types.Contains(s.UserSettingTypeId))
            .ToDictionaryAsync(s => s.UserSettingTypeId, s => s.Value, ct);

    private bool Read(Dictionary<int, string?> values, int type)
    {
        var parsed = EmailPreference.Parse(values.GetValueOrDefault(type));
        if (parsed == EmailPreferenceValue.Unrecognized)
        {
            telemetry.TrackEvent("EmailPreference_Unrecognized", new Dictionary<string, string>
            {
                ["settingTypeId"] = type.ToString(CultureInfo.InvariantCulture),
            });
        }

        return parsed == EmailPreferenceValue.Enabled;
    }

    private static DateTimeOffset? ParseTimestamp(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
}
