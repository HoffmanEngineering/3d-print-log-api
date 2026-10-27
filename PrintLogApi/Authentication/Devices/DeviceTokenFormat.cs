using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace PrintLogApi.Authentication.Devices;

/// <summary>
/// The device token (#148): <c>pld_</c> followed by 32 random bytes in base64url (43 characters).
/// The prefix makes a leaked token recognizable to secret scanners and lets the JWT bearer scheme
/// step aside for it without trying to parse it.
/// </summary>
public static class DeviceTokenFormat
{
    public const string Prefix = "pld_";

    private const int RandomBytes = 32;

    /// <summary>The length of a well-formed token, prefix included.</summary>
    public static readonly int Length = Prefix.Length + Base64Url.GetEncodedLength(RandomBytes);

    public static string NewToken() => Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RandomBytes));

    /// <summary>Whether a bearer value is meant as a device token, well-formed or not.</summary>
    public static bool LooksLikeDeviceToken(string? bearer) =>
        bearer is not null && bearer.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The lookup and storage value: SHA-256, lowercase hex.</summary>
    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// The bearer value from an <c>Authorization</c> header, when its scheme is Bearer and the
    /// value is a device token; otherwise null, and the request is none of this scheme's business.
    /// </summary>
    public static string? FromAuthorizationHeader(string? header)
    {
        const string bearer = "Bearer ";
        if (header is null || !header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var value = header[bearer.Length..].Trim();
        return LooksLikeDeviceToken(value) ? value : null;
    }
}
