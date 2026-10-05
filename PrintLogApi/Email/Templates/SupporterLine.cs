namespace PrintLogApi.Email.Templates;

/// <summary>The one footer sentence about supporting 3D Print Log. Only the monthly recap shows one.</summary>
public enum SupporterLine
{
    /// <summary>Nothing: onboarding (first impressions) and printer alerts (help, not sales).</summary>
    None,

    /// <summary>A free user: one sentence linking to the subscription page.</summary>
    Ask,

    /// <summary>A Pro member: a thank-you instead of an ask.</summary>
    Thanks,
}

/// <summary>Which way a month-over-month change went. Declines are shown neutrally, never in red.</summary>
public enum Trend
{
    Up,
    Down,
    Flat,
}
