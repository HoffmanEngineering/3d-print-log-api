namespace PrintLogApi.Exceptions;

/// <summary>
/// A comment refused by <see cref="Models.CommentLimits"/>. <see cref="RetryAfter"/> is set for a
/// per-commenter rate limit (answered 429) and null for the per-print total (answered 400, since
/// waiting does not help).
/// </summary>
public class CommentLimitExceededException(string message, TimeSpan? retryAfter = null)
    : Exception(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
