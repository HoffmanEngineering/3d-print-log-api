using System;
using System.ComponentModel.DataAnnotations;

namespace PrintLogApi.Models
{
    /// <summary>
    /// Maps a client-supplied idempotency key to the entity a write tool created, so a retried
    /// tool call returns the originally created ENTITY instead of creating a duplicate.
    /// <para>
    /// Not a response snapshot: only the entity id and a request fingerprint are stored, and a
    /// replay re-reads the entity. If it was edited after the original call, the replay returns its
    /// CURRENT state — same entity, newer representation. Retry safety here means "no duplicate row",
    /// not "byte-identical response".
    /// </para>
    /// Not a <see cref="TimestampEntity"/>: <see cref="CreatedAt"/> is set explicitly.
    /// </summary>
    public class McpIdempotencyRecord
    {
        public long Id { get; set; }

        public long UserId { get; set; }

        [Required]
        [StringLength(64)]
        public string ToolName { get; set; } = null!;

        [Required]
        [StringLength(200)]
        public string IdempotencyKey { get; set; } = null!;

        /// <summary>
        /// Lowercase-hex SHA-256 over the canonical serialization of the create-tool arguments that
        /// produced the referenced entity. Null only for hypothetical pre-migration rows (none exist
        /// while unreleased); a null fingerprint replays without comparison.
        /// </summary>
        [StringLength(64)]
        public string? RequestFingerprint { get; set; }

        /// <summary>
        /// The entity this key created. Exactly one of the five is set, decided by
        /// <see cref="ToolName"/>: create_print writes <see cref="CreatedPrintId"/>, create_material
        /// writes <see cref="CreatedFilamentId"/>, create_printer writes
        /// <see cref="CreatedPrinterId"/>, create_project writes <see cref="CreatedProjectId"/>,
        /// create_feedback writes <see cref="CreatedFeedbackId"/>.
        /// All nullable because one table serves five tools.
        /// <para>
        /// There is no check constraint. What makes the rule true is that every record is built by
        /// <c>McpIdempotencyRecordFactory</c>, which sets exactly one target and asserts it. What
        /// makes a violation harmless is that every lookup is scoped by ToolName and reads only its
        /// own field, treating a null there as a dangling record. Do not rely on the other fields
        /// being null; rely on never reading them.
        /// </para>
        /// </summary>
        public long? CreatedPrintId { get; set; }

        public Guid? CreatedFilamentId { get; set; }

        public long? CreatedPrinterId { get; set; }

        public Guid? CreatedProjectId { get; set; }

        public Guid? CreatedFeedbackId { get; set; }

        public DateTimeOffset CreatedAt { get; set; }
    }
}
