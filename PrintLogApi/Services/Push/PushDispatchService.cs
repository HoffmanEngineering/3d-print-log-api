using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;

namespace PrintLogApi.Services.Push;

public class PushDispatchService(
    PrintLogContext context,
    IDeviceTokenService deviceTokenService,
    IFcmClient fcmClient,
    ILogger<PushDispatchService> logger) : IPushDispatchService
{
    /// <summary>
    /// Pushable notification types mapped to the UserSettingType id that governs them. One
    /// map serves both eligibility and the preference lookup, so the two can never disagree.
    /// Adding a type here plus seeding its UserSettingType row is the whole cost of making
    /// it pushable.
    /// </summary>
    private static readonly Dictionary<NotificationType, int> PushEligibleTypes = new()
    {
        [NotificationType.PrintCompleted] = 15,
        [NotificationType.PrintFailed] = 16,
    };

    public async Task DispatchForNotification(Notification notification, CancellationToken ct = default)
    {
        try
        {
            if (!PushEligibleTypes.TryGetValue(notification.Type, out var settingTypeId))
            {
                return;
            }

            var setting = await context.UserSettings
                .Where(s => s.UserId == notification.UserId && s.UserSettingTypeId == settingTypeId)
                .Select(s => s.Value)
                .SingleOrDefaultAsync(ct);

            if (!PushPreference.IsEnabled(setting))
            {
                return;
            }

            var tokens = await deviceTokenService.GetTokensForUser(notification.UserId);
            if (tokens.Count == 0)
            {
                return;
            }

            var data = new Dictionary<string, string>
            {
                ["notificationId"] = notification.Id.ToString(),
                ["type"] = ((int)notification.Type).ToString()
            };

            // IDs only, never a URL. ActionUrl values are relative ("/prints/42"), so a
            // native loadUrl of one would be invalid, and accepting a full URL from a message
            // payload would be a navigation-target injection.
            if (notification.PrintId.HasValue)
            {
                data["printId"] = notification.PrintId.Value.ToString();
            }

            // SpecifyKind, not ToUniversalTime: CreatedDate is written as UTC but comes back
            // from SQL Server as Unspecified, and converting an Unspecified value shifts it
            // by the server's zone.
            var eventTime = new DateTimeOffset(
                DateTime.SpecifyKind(notification.CreatedDate, DateTimeKind.Utc));

            var messages = tokens
                .Select(t => new FcmMessage(
                    t, notification.Title, notification.Message ?? string.Empty, data, eventTime))
                .ToList();

            var result = await fcmClient.SendAsync(messages, ct);

            if (result.UnregisteredTokens.Count > 0)
            {
                await deviceTokenService.PruneTokens(result.UnregisteredTokens);
            }
        }
        catch (Exception ex)
        {
            // Dispatch runs from printer webhooks and comment posts. An FCM outage must not
            // fail those; the in-app notification is already saved and remains the source of
            // truth. Delivery is best-effort by design — see the spec.
            logger.LogError(ex, "Push dispatch failed for notification {NotificationId}.", notification.Id);
        }
    }
}
