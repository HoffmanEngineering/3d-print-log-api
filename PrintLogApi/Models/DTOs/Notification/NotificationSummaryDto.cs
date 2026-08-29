using PrintLogApi.Models.DTOs.User;

namespace PrintLogApi.Models.DTOs.Notification;

public class NotificationSummaryDto
{
    public Guid Id { get; set; }

    public NotificationType Type { get; set; }

    public string? Title { get; set; }

    public string? Message { get; set; }

    public bool IsRead { get; set; }

    /// <summary>
    /// UTC, with an explicit offset. A bare DateTime serializes without a designator, and
    /// JavaScript reads a designator-less ISO string as local time — which silently shifted
    /// every notification timestamp in the web and mobile clients by the viewer's offset.
    /// </summary>
    public DateTimeOffset CreatedDate { get; set; }

    public string? ActionUrl { get; set; }

    public long? PrintId { get; set; }

    public string? PrintTitle { get; set; }

    public UserSummaryDto? TriggeredByUser { get; set; }
}
