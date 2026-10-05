namespace PrintLogApi.Models.DTOs.Email;

/// <param name="MaskedEmail">e.g. "c•••@gmail.com": enough to recognize, not enough to harvest.</param>
public record EmailPreferencesDto(string MaskedEmail, bool All, bool Onboarding, bool MonthlyRecap, bool PrinterSilent);

public record UpdateEmailPreferencesRequest(bool All, bool Onboarding, bool MonthlyRecap, bool PrinterSilent);

/// <param name="Category">The UserSettingType id that was switched off (22 = all email).</param>
public record UnsubscribeConfirmedDto(int Category);
