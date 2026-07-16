using System;
using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models
{
    /// <summary>
    /// Maps a client-supplied idempotency key to the entity a write tool created, so a retried
    /// tool call returns the original result instead of creating a duplicate. Not a
    /// <see cref="TimestampEntity"/>: <see cref="CreatedAt"/> is set explicitly.
    /// </summary>
    public class McpIdempotencyRecord
    {
        public long Id { get; set; }

        public long UserId { get; set; }

        [Required]
        [StringLength(64)]
        public string ToolName { get; set; }

        [Required]
        [StringLength(200)]
        public string IdempotencyKey { get; set; }

        public long CreatedPrintId { get; set; }

        public DateTimeOffset CreatedAt { get; set; }
    }
}
