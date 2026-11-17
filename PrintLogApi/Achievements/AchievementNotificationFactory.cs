using System.Text.Json;
using PrintLogApi.Models;

namespace PrintLogApi.Achievements;

/// <summary>
/// Builds <see cref="NotificationType.Achievement"/> notifications. The notification is the
/// celebration queue: the UI plays unread ones, so a retroactive grant is created already read.
/// Metadata is versioned (<c>"v":1</c>) so clients can ignore shapes they don't know.
/// </summary>
public static class AchievementNotificationFactory
{
    public const int MetadataVersion = 1;

    public static Notification ForGrant(long userId, AchievementDefinition def, int tier, bool retroactive)
    {
        var threshold = def.Thresholds[tier - 1];
        var message = AchievementCopy.Format(def.UnlockedTemplate, threshold);
        if (def.IsTiered)
        {
            message = $"{AchievementCatalog.TierNames[tier - 1]} · {message}";
        }

        var now = DateTime.UtcNow;
        return new Notification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = NotificationType.Achievement,
            Title = $"Achievement unlocked: {def.Title}",
            Message = message,
            ActionUrl = $"/achievements?badge={def.Key}",
            Metadata = JsonSerializer.Serialize(new { v = MetadataVersion, key = def.Key, tier }),
            IsRead = retroactive,
            ReadDate = retroactive ? now : null,
            CreatedDate = now,
        };
    }

    /// <summary>The one unread notification a launch catch-up adds in place of individual celebrations.</summary>
    public static Notification Summary(long userId, int count) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Type = NotificationType.Achievement,
        Title = $"You've earned {count} achievements",
        Message = "Your print history already earned these. Take a look at your collection.",
        ActionUrl = "/achievements",
        Metadata = JsonSerializer.Serialize(new { v = MetadataVersion, summary = true, count }),
        CreatedDate = DateTime.UtcNow,
    };
}
