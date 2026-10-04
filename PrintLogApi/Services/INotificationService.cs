using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Notification;

namespace PrintLogApi.Services;

public interface INotificationService
{
    // Query methods
    Task<PagedList<NotificationSummaryDto>> GetNotificationsForUser(long userId, PagedRequest pagingRequest, bool? unreadOnly = null, NotificationType? type = null);
    Task<NotificationDetailDto?> GetNotificationById(Guid notificationId, long userId);
    Task<int> GetUnreadCountForUser(long userId);

    /// <summary>All unread notifications and the achievement subset, in one query.</summary>
    Task<(int Total, int Achievements)> GetUnreadCountsForUser(long userId);

    // Mutation methods
    Task<bool> MarkAsRead(Guid notificationId, long userId);
    Task<int> MarkAllAsRead(long userId);
    Task<int> MarkMultipleAsRead(IEnumerable<Guid> notificationIds, long userId);
    Task<bool> DeleteNotification(Guid notificationId, long userId);
    Task<int> DeleteAllNotifications(long userId);

    // Create methods
    //
    // INVARIANT: notification creation must not run inside an ambient transaction. Push
    // dispatch fires immediately after SaveChangesAsync, and a sent push cannot be recalled if
    // the surrounding transaction later rolls back. If a caller ever needs transactional
    // notification creation, add an outbox behind IPushDispatchService rather than moving this
    // call — see the spec's "Delivery is best-effort" section.
    Task<Notification> CreateNotification(long userId, NotificationType type, string title, string message, string? actionUrl = null, long? printId = null, long? commentId = null, long? triggeredByUserId = null, string? metadata = null);
    Task<Notification> CreateCommentNotification(long recipientUserId, long printId, string? printTitle, long commentId, long commenterUserId, string commenterDisplayName, bool isRecipientPrintOwner);
    Task CreateCommentNotifications(IEnumerable<(long RecipientUserId, bool IsRecipientPrintOwner)> recipients, long printId, string? printTitle, long commentId, long commenterUserId, string commenterDisplayName);

    /// <summary>
    /// Persists achievement notifications built by <c>AchievementNotificationFactory</c>.
    /// <para>
    /// Deliberately atomic with the grants: the single <c>SaveChangesAsync</c> here also commits
    /// every <c>UserAchievement</c> row (and the user's catalog version) the caller added to the
    /// same scoped context, so a notification exists exactly when its grant does. A unique-key
    /// conflict therefore surfaces here as a <c>DbUpdateException</c>, for the evaluator to retry.
    /// Push dispatch runs only after that save, as for every other notification.
    /// </para>
    /// </summary>
    Task PersistAchievementNotifications(IReadOnlyList<Notification> notifications, CancellationToken ct = default);
    Task<Notification> CreatePrintCompletedNotification(long userId, long printId, string? printTitle);
    Task<Notification> CreatePrintFailedNotification(long userId, long printId, string? printTitle);
    Task<Notification> CreateApiKeyCreatedNotification(long userId, string? keyDescription);
    Task<Notification> CreateApiKeyDeletedNotification(long userId, string? keyDescription);
    Task<Notification> CreateSubscriptionActivatedNotification(long userId, string planDisplayName);
    Task<Notification> CreateSubscriptionPaymentFailedNotification(long userId);
    Task<Notification> CreateSubscriptionCanceledNotification(long userId);
}
