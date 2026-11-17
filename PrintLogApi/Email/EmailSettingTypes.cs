namespace PrintLogApi.Email;

/// <summary>Seeded <c>UserSettingTypes</c> ids for email preferences. Values are permanent once shipped.</summary>
public static class EmailSettingTypes
{
    /// <summary>Master switch for all non-required email.</summary>
    public const int All = 22;

    public const int Onboarding = 23;

    public const int MonthlyRecap = 24;

    public const int PrinterSilent = 25;

    /// <summary>ISO timestamp at which the user dismissed the in-app email notice.</summary>
    public const int NoticeSeenAt = 26;
}
