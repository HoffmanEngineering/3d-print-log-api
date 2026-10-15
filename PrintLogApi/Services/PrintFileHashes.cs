namespace PrintLogApi.Services;

/// <summary>
/// <see cref="Models.Print.FileHash"/> on the wire: exactly 20 bytes, as 40 hex digits in either
/// case. OctoPrint sends the SHA-1 of the G-code file; a connector that cannot hash the file sends
/// its own 20-byte identity for it (printlog-bridge#40), which stays the same for every job of one
/// file and changes when the file is re-sliced. Either way, two prints of the same user with the
/// same hash are prints of the same file. A hash is never compared across users.
/// </summary>
public static class PrintFileHashes
{
    public const int Length = 20;

    public const string ValidationMessage = "fileHash must be 40 hex digits (20 bytes).";

    public static bool IsValid(string hex) => Parse(hex) is not null;

    /// <summary>The hash's bytes, or null when <paramref name="hex"/> is missing or malformed.</summary>
    public static byte[]? Parse(string? hex)
    {
        if (hex is null || hex.Length != Length * 2 || !hex.All(char.IsAsciiHexDigit))
        {
            return null;
        }
        return Convert.FromHexString(hex);
    }
}
