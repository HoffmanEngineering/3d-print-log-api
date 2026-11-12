namespace PrintLogApi.Models;

/// <summary>
/// Limits on comments on a print, checked when one is posted.
///
/// <para>These are defaults chosen during a security review (November 2026), not product
/// decisions: before them one account could post any number of comments on a print, each one
/// notifying the owner and every earlier commenter, and every reader of the print downloading all
/// of them. Tune them freely; nothing else depends on the exact numbers.</para>
///
/// <para>They are counted from the comments' own <c>CreatedDate</c> in the database, not by an
/// in-memory limiter, so they hold across restarts and instances. Two comments posted at the same
/// instant can both pass a check, so a limit can be exceeded by the few that race it.</para>
/// </summary>
public static class CommentLimits
{
    /// <summary>Comments one user may post on one print within <see cref="ShortWindow"/>.</summary>
    public const int PerCommenterShortWindowMax = 10;

    /// <summary>The window <see cref="PerCommenterShortWindowMax"/> counts over.</summary>
    public static readonly TimeSpan ShortWindow = TimeSpan.FromMinutes(10);

    /// <summary>Comments one user may post on one print within <see cref="DayWindow"/>.</summary>
    public const int PerCommenterDayMax = 50;

    /// <summary>The window <see cref="PerCommenterDayMax"/> counts over.</summary>
    public static readonly TimeSpan DayWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// Comments a print may hold in total. The print's owner is exempt from the per-commenter
    /// limits on their own print (they reply to people), but not from this one.
    /// </summary>
    public const int MaxCommentsPerPrint = 1000;

    /// <summary>The ProblemDetails <c>code</c> of a 429 for a per-commenter limit.</summary>
    public const string RateLimitedCode = "comment_rate_limited";

    /// <summary>The ProblemDetails <c>code</c> of a 400 for <see cref="MaxCommentsPerPrint"/>.</summary>
    public const string TotalReachedCode = "comment_limit_reached";
}
