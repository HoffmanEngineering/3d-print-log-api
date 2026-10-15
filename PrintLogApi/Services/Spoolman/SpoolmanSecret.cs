using System.Security.Cryptography;
using System.Text;

namespace PrintLogApi.Services.Spoolman;

/// <summary>
/// The Spoolman connection secret (#175). It is pasted into a URL as
/// <c>https://printer-12:&lt;secret&gt;@api.3dprintlog.com/spoolman</c>, so it must survive as URL
/// userinfo untouched: base64url without padding, never <c>:</c> <c>@</c> <c>/</c> <c>%</c>
/// <c>+</c> or <c>=</c>. 32 random bytes, 43 characters.
/// </summary>
public static class SpoolmanSecret
{
    public const int ByteLength = 32;
    public const string HashAlgorithm = "SHA256";

    public static string Generate() => Base64Url(RandomNumberGenerator.GetBytes(ByteLength));

    /// <summary>
    /// Lower-case hex SHA-256: the stored form and the lookup key. A plain hash is enough here,
    /// unlike for a password: the secret is 256 random bits, so there is nothing to brute-force.
    /// </summary>
    public static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
