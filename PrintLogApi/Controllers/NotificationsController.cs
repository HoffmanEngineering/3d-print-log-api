using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Notification;
using PrintLogApi.Services;

namespace PrintLogApi.Controllers
{
    /// <summary>
    /// Operations involving user notifications.
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class NotificationsController : ControllerBase
    {
        private readonly INotificationService _notificationService;

        public NotificationsController(INotificationService notificationService)
        {
            _notificationService = notificationService;
        }

        /// <summary>
        /// Get a paged list of notifications for the current user.
        /// </summary>
        /// <param name="pagingRequest">The paging request.</param>
        /// <param name="unreadOnly">If true, only return unread notifications.</param>
        /// <returns>A paged list of notifications.</returns>
        /// <response code="200">A paged list of notifications.</response>
        /// <response code="401">Returned if the user is not authenticated.</response>
        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult<PagedList<NotificationSummaryDto>>> GetNotifications(
            [FromQuery] PagedRequest pagingRequest,
            [FromQuery] bool? unreadOnly = null)
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var notifications = await _notificationService.GetNotificationsForUser(userId.Value, pagingRequest, unreadOnly);
            return Ok(notifications);
        }

        /// <summary>
        /// Get the count of unread notifications for the current user.
        /// </summary>
        /// <returns>The unread notification count.</returns>
        /// <response code="200">The unread notification count.</response>
        /// <response code="401">Returned if the user is not authenticated.</response>
        [HttpGet("unread-count")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult<NotificationUnreadCountDto>> GetUnreadCount()
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var count = await _notificationService.GetUnreadCountForUser(userId.Value);
            return Ok(new NotificationUnreadCountDto { UnreadCount = count });
        }

        /// <summary>
        /// Get a single notification by ID.
        /// </summary>
        /// <param name="id">The notification ID.</param>
        /// <returns>The notification details.</returns>
        /// <response code="200">The notification details.</response>
        /// <response code="401">Returned if the user is not authenticated.</response>
        /// <response code="404">Returned if the notification is not found.</response>
        [HttpGet("{id}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<NotificationDetailDto>> GetNotification(Guid id)
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var notification = await _notificationService.GetNotificationById(id, userId.Value);
            if (notification == null)
            {
                return NotFound();
            }

            return Ok(notification);
        }

        /// <summary>
        /// Mark a single notification as read.
        /// </summary>
        /// <param name="id">The notification ID.</param>
        /// <returns>No content on success.</returns>
        /// <response code="204">The notification was marked as read.</response>
        /// <response code="401">Returned if the user is not authenticated.</response>
        /// <response code="404">Returned if the notification is not found.</response>
        [HttpPut("{id}/read")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> MarkAsRead(Guid id)
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var success = await _notificationService.MarkAsRead(id, userId.Value);
            if (!success)
            {
                return NotFound();
            }

            return NoContent();
        }

        /// <summary>
        /// Mark all notifications as read for the current user.
        /// </summary>
        /// <returns>No content on success.</returns>
        /// <response code="204">All notifications were marked as read.</response>
        /// <response code="401">Returned if the user is not authenticated.</response>
        [HttpPut("read-all")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult> MarkAllAsRead()
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            await _notificationService.MarkAllAsRead(userId.Value);
            return NoContent();
        }

        /// <summary>
        /// Mark multiple notifications as read.
        /// </summary>
        /// <param name="dto">The notification IDs to mark as read.</param>
        /// <returns>No content on success.</returns>
        /// <response code="204">The notifications were marked as read.</response>
        /// <response code="400">Returned if the request body is invalid.</response>
        /// <response code="401">Returned if the user is not authenticated.</response>
        [HttpPut("read")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult> MarkMultipleAsRead([FromBody] MarkNotificationsReadDto dto)
        {
            const int MaxNotificationIds = 100;

            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            if (dto?.NotificationIds == null || dto.NotificationIds.Count == 0)
            {
                return BadRequest("NotificationIds is required and must not be empty.");
            }

            if (dto.NotificationIds.Count > MaxNotificationIds)
            {
                return BadRequest($"Cannot mark more than {MaxNotificationIds} notifications as read at once.");
            }

            await _notificationService.MarkMultipleAsRead(dto.NotificationIds, userId.Value);
            return NoContent();
        }

        /// <summary>
        /// Delete a single notification.
        /// </summary>
        /// <param name="id">The notification ID.</param>
        /// <returns>No content on success.</returns>
        /// <response code="204">The notification was deleted.</response>
        /// <response code="401">Returned if the user is not authenticated.</response>
        /// <response code="404">Returned if the notification is not found.</response>
        [HttpDelete("{id}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> DeleteNotification(Guid id)
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var success = await _notificationService.DeleteNotification(id, userId.Value);
            if (!success)
            {
                return NotFound();
            }

            return NoContent();
        }

        /// <summary>
        /// Delete all notifications for the current user.
        /// </summary>
        /// <returns>No content on success.</returns>
        /// <response code="204">All notifications were deleted.</response>
        /// <response code="401">Returned if the user is not authenticated.</response>
        [HttpDelete]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult> DeleteAllNotifications()
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            await _notificationService.DeleteAllNotifications(userId.Value);
            return NoContent();
        }
    }
}
