using System;
using PrintLogApi.Models;

namespace PrintLogApi.Mcp
{
    /// <summary>
    /// The single construction path for <see cref="McpIdempotencyRecord"/>. Five nullable target
    /// columns share one table and exactly one must be set per record; centralizing construction is
    /// what makes that true, because there is no schema constraint behind it.
    /// </summary>
    public static class McpIdempotencyRecordFactory
    {
        public static McpIdempotencyRecord ForPrint(long userId, string key, string fingerprint, long printId) =>
            Build(userId, "create_print", key, fingerprint, r => r.CreatedPrintId = printId);

        public static McpIdempotencyRecord ForMaterial(long userId, string key, string fingerprint, Guid filamentId) =>
            Build(userId, "create_material", key, fingerprint, r => r.CreatedFilamentId = filamentId);

        public static McpIdempotencyRecord ForPrinter(long userId, string key, string fingerprint, long printerId) =>
            Build(userId, "create_printer", key, fingerprint, r => r.CreatedPrinterId = printerId);

        public static McpIdempotencyRecord ForProject(long userId, string key, string fingerprint, Guid projectId) =>
            Build(userId, "create_project", key, fingerprint, r => r.CreatedProjectId = projectId);

        public static McpIdempotencyRecord ForFeedback(long userId, string key, string fingerprint, Guid feedbackId) =>
            Build(userId, "create_feedback", key, fingerprint, r => r.CreatedFeedbackId = feedbackId);

        /// <summary>
        /// Counts the non-null targets rather than chaining XOR: a pairwise XOR of several operands is
        /// true for an ODD number of non-null values, which would wave through the worst cases.
        /// <para>
        /// Throws <see cref="InvalidOperationException"/>, not <see cref="McpToolException"/>: a
        /// record with the wrong number of targets is a bug in this server, never something a caller
        /// can provoke, so it must not be reported to an agent as bad input.
        /// </para>
        /// </summary>
        public static void RequireExactlyOneTarget(McpIdempotencyRecord record)
        {
            var targets = (record.CreatedPrintId.HasValue ? 1 : 0)
                + (record.CreatedFilamentId.HasValue ? 1 : 0)
                + (record.CreatedPrinterId.HasValue ? 1 : 0)
                + (record.CreatedProjectId.HasValue ? 1 : 0)
                + (record.CreatedFeedbackId.HasValue ? 1 : 0);
            if (targets != 1)
            {
                throw new InvalidOperationException(
                    $"An idempotency record must reference exactly one created entity, but {targets} were set.");
            }
        }

        private static McpIdempotencyRecord Build(
            long userId, string toolName, string key, string fingerprint, Action<McpIdempotencyRecord> setTarget)
        {
            var record = new McpIdempotencyRecord
            {
                UserId = userId,
                ToolName = toolName,
                IdempotencyKey = key,
                RequestFingerprint = fingerprint,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            setTarget(record);
            RequireExactlyOneTarget(record);
            return record;
        }
    }
}
