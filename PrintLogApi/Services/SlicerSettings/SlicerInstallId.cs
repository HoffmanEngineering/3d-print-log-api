namespace PrintLogApi.Services.SlicerSettings;

/// <summary>
/// The OrcaSlicer plugin's install id: exactly 32 lowercase hex characters (Python's
/// <c>uuid4().hex</c>). Checked by ordinal character, so no other case, digit set or format passes.
/// </summary>
public static class SlicerInstallId
{
    public const int Length = 32;

    public static bool IsValid(string? value)
    {
        if (value is null || value.Length != Length)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}
