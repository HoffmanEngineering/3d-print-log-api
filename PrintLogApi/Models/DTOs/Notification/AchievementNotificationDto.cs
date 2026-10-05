namespace PrintLogApi.Models.DTOs.Notification;

/// <summary>
/// The typed form of an <see cref="NotificationType.Achievement"/> notification's metadata. Either
/// a single grant (<see cref="Key"/> and <see cref="Tier"/>) or the launch summary
/// (<see cref="Summary"/> and <see cref="Count"/>).
/// </summary>
public class AchievementNotificationDto
{
    public string? Key { get; set; }

    public int? Tier { get; set; }

    public bool Summary { get; set; }

    public int? Count { get; set; }
}
