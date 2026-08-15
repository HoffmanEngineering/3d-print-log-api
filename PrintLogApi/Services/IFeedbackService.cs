#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using PrintLogApi.Models;

namespace PrintLogApi.Services
{
    /// <summary>
    /// How a piece of feedback reached us. Not persisted: each caller knows its own path, and the
    /// value exists only to label the notification email.
    /// </summary>
    public enum FeedbackSource
    {
        Website,
        McpAgent,
    }

    /// <summary>
    /// The single persist-and-notify path for feedback. Both the website endpoint and the MCP tool
    /// funnel through it so the notification body cannot drift between them.
    /// </summary>
    public interface IFeedbackService
    {
        /// <summary>
        /// Records feedback and notifies the configured address. Used by the website endpoint, where
        /// <paramref name="email"/> is the address the user typed into the form (optional).
        /// </summary>
        Task<Feedback> CreateFeedback(
            long userId, Feedback.FeedbackType type, string? email, string? note, CancellationToken ct);

        /// <summary>
        /// Records feedback on behalf of an agent. The idempotency key is REQUIRED here, unlike the
        /// other create tools: a keyless retry would both insert a duplicate row and send a duplicate
        /// notification, and neither can be undone from the UI. A replay never re-notifies.
        /// </summary>
        Task<Mcp.CreateFeedbackResult> CreateFeedbackForMcp(
            long userId, Feedback.FeedbackType type, string? note, string idempotencyKey, CancellationToken ct);
    }
}
