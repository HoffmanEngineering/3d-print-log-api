using System.Text;

namespace PrintLogApi.Models;

/// <summary>
/// The one normalization every tag UID goes through before it is stored or looked up (#147), so
/// <c>04:A2:3f:11</c> from a phone and <c>04A23F11</c> from Klipper are the same tag.
/// </summary>
public static class TagUid
{
    /// <summary>Longest stored UID, in hex characters (32 bytes). NFC UIDs are 4, 7 or 10 bytes.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// Most tags one filament may carry. A Bambu spool has two, one per side; the cap only stops
    /// an unbounded list reaching the database.
    /// </summary>
    public const int MaxPerFilament = 16;

    /// <summary>
    /// Uppercase hex with separators (<c>:</c>, <c>-</c>, <c>.</c>, <c>_</c>, whitespace) removed,
    /// or null when what is left is empty, is not hex, is not a whole number of bytes, or is
    /// longer than <see cref="MaxLength"/>. A <c>0x</c> prefix is rejected rather than guessed at.
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (input is null)
        {
            return null;
        }

        var builder = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (c is ':' or '-' or '.' or '_' || char.IsWhiteSpace(c))
            {
                continue;
            }

            if (!char.IsAsciiHexDigit(c))
            {
                return null;
            }

            builder.Append(char.ToUpperInvariant(c));
        }

        if (builder.Length == 0 || builder.Length % 2 != 0 || builder.Length > MaxLength)
        {
            return null;
        }

        return builder.ToString();
    }
}
