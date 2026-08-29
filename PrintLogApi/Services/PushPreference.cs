namespace PrintLogApi.Services;

/// <summary>
/// Interpretation of the strings stored in UserSettings for push preferences.
///
/// UserSetting.Value is a free-form nullable string and the DTOs accept anything, so the
/// meaning of a stored value must be defined in exactly one place or the UI and the
/// dispatcher will eventually disagree about what "off" looks like.
/// </summary>
public static class PushPreference
{
    public const string Enabled = "true";
    public const string Disabled = "false";

    /// <summary>
    /// Absence, emptiness, and anything unrecognised all mean enabled. Users opt out of
    /// push here; they opt in via the Android notification permission.
    /// </summary>
    public static bool IsEnabled(string? storedValue)
        => !string.Equals(storedValue?.Trim(), Disabled, StringComparison.OrdinalIgnoreCase);
}
