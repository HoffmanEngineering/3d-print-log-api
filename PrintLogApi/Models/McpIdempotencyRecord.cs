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

        /// <summary>
        /// Lowercase-hex SHA-256 over the canonical serialization of the create-tool arguments that
        /// produced <see cref="CreatedPrintId"/>. Null only for hypothetical pre-migration rows
        /// (none exist while unreleased); a null fingerprint replays without comparison.
        /// </summary>
        [StringLength(64)]
        public string RequestFingerprint { get; set; }

        public long CreatedPrintId { get; set; }

        public DateTimeOffset CreatedAt { get; set; }
    }
}
