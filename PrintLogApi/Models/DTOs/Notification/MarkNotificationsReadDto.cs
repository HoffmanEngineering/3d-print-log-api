using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models.DTOs.Notification
{
    public class MarkNotificationsReadDto
    {
        [Required]
        public List<Guid>? NotificationIds { get; set; }
    }
}
