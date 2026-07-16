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
        /// produced the referenced entity. Null only for hypothetical pre-migration rows (none exist
        /// while unreleased); a null fingerprint replays without comparison.
        /// </summary>
        [StringLength(64)]
        public string RequestFingerprint { get; set; }

        /// <summary>
        /// The entity this key created. By convention exactly one is set, decided by
        /// <see cref="ToolName"/>: create_print writes <see cref="CreatedPrintId"/>, create_material
        /// writes <see cref="CreatedFilamentId"/>. Both nullable because one table serves both tools.
        /// <para>
        /// Nothing enforces the exactly-one rule — there is no check constraint and no validation
        /// hook. What makes it safe is that every lookup is scoped by ToolName and reads only its own
        /// field, treating a null there as a dangling record. Do not rely on the other field being
        /// null; rely on never reading it.
        /// </para>
        /// </summary>
        public long? CreatedPrintId { get; set; }

        public Guid? CreatedFilamentId { get; set; }

        public DateTimeOffset CreatedAt { get; set; }
    }
}
