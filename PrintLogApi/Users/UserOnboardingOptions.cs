namespace PrintLogApi.Users;

/// <summary>
/// Settings for the new-user onboarding flow.
/// </summary>
public class UserOnboardingOptions
{
    public const string SectionName = "Onboarding";

    /// <summary>
    /// Accounts created on or after this moment are onboarded. Null turns the feature off.
    /// </summary>
    public DateTimeOffset? LaunchDate { get; set; }

    public bool IsEligible(DateTimeOffset? createdDate) =>
        LaunchDate != null && createdDate != null && createdDate >= LaunchDate;
}
