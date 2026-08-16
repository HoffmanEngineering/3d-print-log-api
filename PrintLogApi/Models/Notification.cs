using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrintLogApi.Models
{
    public class Notification
    {
        [Key]
        public Guid Id { get; set; }

        public long UserId { get; set; }
        [ForeignKey("UserId")]
        public User User { get; set; } = null!;

        public NotificationType Type { get; set; }

        [Required]
        [MaxLength(200)]
        public string Title { get; set; } = null!;

        [MaxLength(1000)]
        public string? Message { get; set; }

        public bool IsRead { get; set; }

        public DateTime CreatedDate { get; set; }

        public DateTime? ReadDate { get; set; }

        [MaxLength(500)]
        public string? ActionUrl { get; set; }

        public long? PrintId { get; set; }
        [ForeignKey("PrintId")]
        public Print? Print { get; set; }

        public long? CommentId { get; set; }
        [ForeignKey("CommentId")]
        public Comment? Comment { get; set; }

        public long? TriggeredByUserId { get; set; }
        [ForeignKey("TriggeredByUserId")]
        public User? TriggeredByUser { get; set; }

        /// <summary>
        /// JSON string for storing additional metadata for extensibility (e.g., achievement data)
        /// </summary>
        public string? Metadata { get; set; }
    }
}
