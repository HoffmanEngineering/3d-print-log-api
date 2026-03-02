using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Notification;

namespace PrintLogApi.Services
{
    public class NotificationService : INotificationService
    {
        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;

        public NotificationService(PrintLogContext context, IMapper mapper)
        {
            _context = context;
            _mapper = mapper;
        }

        public async Task<PagedList<NotificationSummaryDto>> GetNotificationsForUser(long userId, PagedRequest pagingRequest, bool? unreadOnly = null)
        {
            var query = _context.Notifications
                .Where(n => n.UserId == userId);

            if (unreadOnly == true)
            {
                query = query.Where(n => !n.IsRead);
            }

            var orderedQuery = query
                .OrderByDescending(n => n.CreatedDate)
                .ProjectTo<NotificationSummaryDto>(_mapper.ConfigurationProvider);

            return await PagedList<NotificationSummaryDto>.CreateAsync(
                orderedQuery,
                pagingRequest.PageNumber,
                pagingRequest.PageSize);
        }

        public async Task<NotificationDetailDto> GetNotificationById(Guid notificationId, long userId)
        {
            var notification = await _context.Notifications
                .Where(n => n.Id == notificationId && n.UserId == userId)
                .Include(n => n.Print)
                .Include(n => n.TriggeredByUser)
                .FirstOrDefaultAsync();

            if (notification == null)
            {
                return null;
            }

            return _mapper.Map<NotificationDetailDto>(notification);
        }

        public async Task<int> GetUnreadCountForUser(long userId)
        {
            return await _context.Notifications
                .Where(n => n.UserId == userId && !n.IsRead)
                .CountAsync();
        }

        public async Task<bool> MarkAsRead(Guid notificationId, long userId)
        {
            var notification = await _context.Notifications
                .Where(n => n.Id == notificationId && n.UserId == userId)
                .FirstOrDefaultAsync();

            if (notification == null)
            {
                return false;
            }

            if (!notification.IsRead)
            {
                notification.IsRead = true;
                notification.ReadDate = DateTime.UtcNow;
                await _context.SaveChangesAsync();
            }

            return true;
        }

        public async Task<int> MarkAllAsRead(long userId)
        {
            var now = DateTime.UtcNow;
            return await _context.Notifications
                .Where(n => n.UserId == userId && !n.IsRead)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(n => n.IsRead, true)
                    .SetProperty(n => n.ReadDate, now));
        }

        public async Task<int> MarkMultipleAsRead(IEnumerable<Guid> notificationIds, long userId)
        {
            var idList = notificationIds.ToList();
            var now = DateTime.UtcNow;
            return await _context.Notifications
                .Where(n => n.UserId == userId && idList.Contains(n.Id) && !n.IsRead)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(n => n.IsRead, true)
                    .SetProperty(n => n.ReadDate, now));
        }

        public async Task<bool> DeleteNotification(Guid notificationId, long userId)
        {
            var deleted = await _context.Notifications
                .Where(n => n.Id == notificationId && n.UserId == userId)
                .ExecuteDeleteAsync();

            return deleted > 0;
        }

        public async Task<int> DeleteAllNotifications(long userId)
        {
            return await _context.Notifications
                .Where(n => n.UserId == userId)
                .ExecuteDeleteAsync();
        }

        public async Task<Notification> CreateNotification(
            long userId,
            NotificationType type,
            string title,
            string message,
            string actionUrl = null,
            long? printId = null,
            long? commentId = null,
            long? triggeredByUserId = null,
            string metadata = null)
        {
            var notification = new Notification
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Type = type,
                Title = title,
                Message = message,
                ActionUrl = actionUrl,
                PrintId = printId,
                CommentId = commentId,
                TriggeredByUserId = triggeredByUserId,
                Metadata = metadata,
                IsRead = false,
                CreatedDate = DateTime.UtcNow
            };

            _context.Notifications.Add(notification);
            await _context.SaveChangesAsync();

            return notification;
        }

        public async Task<Notification> CreateCommentNotification(
            long recipientUserId,
            long printId,
            string printTitle,
            long commentId,
            long commenterUserId,
            string commenterDisplayName,
            bool isRecipientPrintOwner)
        {
            string title;
            string message;

            if (isRecipientPrintOwner)
            {
                title = "New comment on your print";
                message = $"{commenterDisplayName} commented on \"{printTitle}\"";
            }
            else
            {
                title = "New reply on a print you commented on";
                message = $"{commenterDisplayName} also commented on \"{printTitle}\"";
            }

            var actionUrl = $"/prints/{printId}#comment-{commentId}";

            return await CreateNotification(
                recipientUserId,
                NotificationType.Comment,
                title,
                message,
                actionUrl,
                printId,
                commentId,
                commenterUserId);
        }

        public async Task<Notification> CreatePrintCompletedNotification(long userId, long printId, string printTitle)
        {
            var title = "Print completed";
            var message = $"Your print \"{printTitle}\" has completed successfully";
            var actionUrl = $"/prints/{printId}";

            return await CreateNotification(
                userId,
                NotificationType.PrintCompleted,
                title,
                message,
                actionUrl,
                printId);
        }

        public async Task<Notification> CreatePrintFailedNotification(long userId, long printId, string printTitle)
        {
            var title = "Print failed";
            var message = $"Your print \"{printTitle}\" has failed";
            var actionUrl = $"/prints/{printId}";

            return await CreateNotification(
                userId,
                NotificationType.PrintFailed,
                title,
                message,
                actionUrl,
                printId);
        }

        public async Task<Notification> CreateApiKeyCreatedNotification(long userId, string keyDescription)
        {
            var title = "New API key created";
            var message = string.IsNullOrWhiteSpace(keyDescription)
                ? "A new API key was created for your account"
                : $"A new API key \"{keyDescription}\" was created for your account";
            var actionUrl = "/api-keys";

            return await CreateNotification(
                userId,
                NotificationType.SystemAnnouncement,
                title,
                message,
                actionUrl);
        }

        public async Task<Notification> CreateApiKeyDeletedNotification(long userId, string keyDescription)
        {
            var title = "API key deleted";
            var message = string.IsNullOrWhiteSpace(keyDescription)
                ? "An API key was deleted from your account"
                : $"The API key \"{keyDescription}\" was deleted from your account";
            var actionUrl = "/api-keys";

            return await CreateNotification(
                userId,
                NotificationType.SystemAnnouncement,
                title,
                message,
                actionUrl);
        }

        public Task<Notification> CreateSubscriptionActivatedNotification(long userId, string planDisplayName)
        {
            return CreateNotification(
                userId,
                NotificationType.SubscriptionActivated,
                "Pro subscription activated",
                $"Welcome to {planDisplayName}! You now have access to all Pro features.",
                actionUrl: "/settings/subscription");
        }

        public Task<Notification> CreateSubscriptionPaymentFailedNotification(long userId)
        {
            return CreateNotification(
                userId,
                NotificationType.SubscriptionPaymentFailed,
                "Payment failed",
                "Your Pro subscription payment failed. Please update your payment method to keep Pro access.",
                actionUrl: "/settings/subscription");
        }

        public Task<Notification> CreateSubscriptionCanceledNotification(long userId)
        {
            return CreateNotification(
                userId,
                NotificationType.SubscriptionCanceled,
                "Pro subscription ended",
                "Your Pro subscription has ended. Upgrade again to restore Pro features.",
                actionUrl: "/settings/subscription");
        }
    }
}
