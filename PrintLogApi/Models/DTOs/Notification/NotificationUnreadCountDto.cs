namespace PrintLogApi.Models.DTOs.Notification;

public class NotificationUnreadCountDto
{
    public int UnreadCount { get; set; }

    /// <summary>
    /// How many of the unread notifications are achievements. The web client's existing poll
    /// reads this to know when there is a celebration to fetch, without a second request.
    /// </summary>
    public int UnreadAchievementCount { get; set; }
}
