using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Notification;

namespace PrintLogApi.Services;

public interface INotificationService
{
    // Query methods
    Task<PagedList<NotificationSummaryDto>> GetNotificationsForUser(long userId, PagedRequest pagingRequest, bool? unreadOnly = null);
    Task<NotificationDetailDto?> GetNotificationById(Guid notificationId, long userId);
    Task<int> GetUnreadCountForUser(long userId);

    // Mutation methods
    Task<bool> MarkAsRead(Guid notificationId, long userId);
    Task<int> MarkAllAsRead(long userId);
    Task<int> MarkMultipleAsRead(IEnumerable<Guid> notificationIds, long userId);
    Task<bool> DeleteNotification(Guid notificationId, long userId);
    Task<int> DeleteAllNotifications(long userId);

    // Create methods
    Task<Notification> CreateNotification(long userId, NotificationType type, string title, string message, string? actionUrl = null, long? printId = null, long? commentId = null, long? triggeredByUserId = null, string? metadata = null);
    Task<Notification> CreateCommentNotification(long recipientUserId, long printId, string? printTitle, long commentId, long commenterUserId, string commenterDisplayName, bool isRecipientPrintOwner);
    Task CreateCommentNotifications(IEnumerable<(long RecipientUserId, bool IsRecipientPrintOwner)> recipients, long printId, string? printTitle, long commentId, long commenterUserId, string commenterDisplayName);
    Task<Notification> CreatePrintCompletedNotification(long userId, long printId, string? printTitle);
    Task<Notification> CreatePrintFailedNotification(long userId, long printId, string? printTitle);
    Task<Notification> CreateApiKeyCreatedNotification(long userId, string? keyDescription);
    Task<Notification> CreateApiKeyDeletedNotification(long userId, string? keyDescription);
    Task<Notification> CreateSubscriptionActivatedNotification(long userId, string planDisplayName);
    Task<Notification> CreateSubscriptionPaymentFailedNotification(long userId);
    Task<Notification> CreateSubscriptionCanceledNotification(long userId);
}
