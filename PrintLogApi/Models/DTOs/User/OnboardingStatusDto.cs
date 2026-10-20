namespace PrintLogApi.Models.DTOs;

/// <summary>
/// Whether the signed-in user goes through onboarding, plus the facts the UI needs to drive it.
/// </summary>
public record OnboardingStatusDto(bool OnboardingEligible, DateTimeOffset? CreatedDate, bool EmailVerified);
