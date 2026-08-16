using PrintLogApi.Models.DTOs.User;

namespace PrintLogApi.Models.DTOs.Notification;

public class NotificationDetailDto
{
    public Guid Id { get; set; }

    public NotificationType Type { get; set; }

    public string? Title { get; set; }

    public string? Message { get; set; }

    public bool IsRead { get; set; }

    public DateTime CreatedDate { get; set; }

    public DateTime? ReadDate { get; set; }

    public string? ActionUrl { get; set; }

    public long? PrintId { get; set; }

    public string? PrintTitle { get; set; }

    public long? CommentId { get; set; }

    public UserSummaryDto? TriggeredByUser { get; set; }

    public string? Metadata { get; set; }
}
