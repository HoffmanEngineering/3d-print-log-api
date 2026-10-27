using System.Text.RegularExpressions;

namespace PrintLogApi.SpoolTags.Labels;

/// <summary>
/// Normalizes a barcode read from a spool label, so one code is stored and voted on under one
/// spelling. Labels carry UPC/EAN codes, Code128 batch codes and Amazon FNSKUs, so anything
/// short and printable is accepted; a URL or markup is not a barcode.
/// </summary>
public static partial class BarcodeValue
{
    public const int MaxLength = 64;
    public const int FormatMaxLength = 32;

    /// <summary>
    /// Trimmed and uppercased. A 12-digit UPC-A becomes the EAN-13 a scanner reports for the same
    /// bars (leading 0). Null when the value is empty, too long or not barcode characters.
    /// </summary>
    public static string? Normalize(string? value)
    {
        var v = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(v) || v.Length > MaxLength || !Allowed().IsMatch(v))
        {
            return null;
        }
        return v.Length == 12 && AllDigits().IsMatch(v) ? "0" + v : v;
    }

    /// <summary>
    /// The symbology as a short lowercase name. A 13-digit value counts as EAN-13 whatever the
    /// reader called it, since a UPC-A was padded to one by <see cref="Normalize"/>.
    /// </summary>
    public static string NormalizeFormat(string? format, string normalizedValue)
    {
        var f = NonAlphanumeric().Replace(format?.ToLowerInvariant() ?? "", "");
        if (f is "upca" or "ean13" && normalizedValue.Length == 13 && AllDigits().IsMatch(normalizedValue))
        {
            return "ean13";
        }
        return f switch
        {
            "ean13" or "ean8" or "upca" or "upce" or "code128" or "code39" or "qrcode" => f,
            _ => "other",
        };
    }

    [GeneratedRegex(@"^[A-Z0-9\-./ ]+$")]
    private static partial Regex Allowed();

    [GeneratedRegex(@"^[0-9]+$")]
    private static partial Regex AllDigits();

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAlphanumeric();
}
