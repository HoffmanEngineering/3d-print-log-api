namespace PrintLogApi.Email;

public enum EmailPreferenceValue
{
    Enabled,
    Disabled,
    Unrecognized,
}

/// <summary>
/// Interpretation of the strings stored in UserSettings for email preferences — the one place the
/// meaning of a stored value is defined, mirroring <c>PushPreference</c> but stricter: absence means
/// on (the owner's opt-out default), while a malformed value is never read as consent.
/// </summary>
public static class EmailPreference
{
    public const string Enabled = "true";
    public const string Disabled = "false";

    public static EmailPreferenceValue Parse(string? storedValue)
    {
        var value = storedValue?.Trim();
        if (string.IsNullOrEmpty(value) || string.Equals(value, Enabled, StringComparison.OrdinalIgnoreCase))
        {
            return EmailPreferenceValue.Enabled;
        }

        return string.Equals(value, Disabled, StringComparison.OrdinalIgnoreCase)
            ? EmailPreferenceValue.Disabled
            : EmailPreferenceValue.Unrecognized;
    }

    public static bool IsEnabled(string? storedValue) => Parse(storedValue) == EmailPreferenceValue.Enabled;

    public static string ToStored(bool enabled) => enabled ? Enabled : Disabled;
}
