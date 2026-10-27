using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace PrintLogApi.Authentication;

/// <summary>
/// Identifies the long-lived credential a connector (the printlog-bridge, a slicer plugin)
/// authenticated with, as opposed to the user it acts for. Its presence is what makes a request
/// connector traffic for the per-credential write budget (<c>ConnectorWriteRateLimiting</c>);
/// an interactive sign-in never carries it, which is how web and mobile app traffic stays out of
/// that budget.
///
/// The connector credentials are a personal API key, added by <c>ApiKeyMiddleware</c>, a Spoolman
/// connection secret, added by <c>SpoolmanBasicAuthenticationHandler</c> (#176), and a device
/// token, added by <c>DeviceTokenMiddleware</c> with a <c>device:</c> value (#148). The limiter
/// treats them all alike.
/// </summary>
public static class ConnectorCredentialClaim
{
    public const string Type = "connector_credential";

    /// <summary>
    /// The value for a personal API key. A domain-separated hash rather than the key, so the
    /// limiter's partition table and anything that happens to log claims never hold a usable
    /// credential, and rather than the stored <c>HashedKey</c>, so it is not a database lookup
    /// value either. Two requests with the same key always map to the same value.
    /// </summary>
    public static string ForApiKey(string key)
        => "api_key:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("connector-credential:" + key)));

    /// <summary>
    /// The value for a Spoolman connection secret (#176), on the same terms as
    /// <see cref="ForApiKey"/>: domain-separated, so it is neither the secret nor the stored
    /// <c>SecretHash</c> that looks it up.
    /// </summary>
    public static string ForSpoolman(string secret)
        => "spoolman:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("connector-credential:spoolman:" + secret)));

    /// <summary>
    /// The value for a device token (#148): the grant's id, which is not a secret. Keyed on the
    /// grant rather than the token, so a re-pair (which rotates the token on the same grant) keeps
    /// the device in its bucket.
    /// </summary>
    public static string ForDeviceGrant(long grantId)
        => "device:" + grantId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The credential the request authenticated with, or null for an interactive user.</summary>
    public static string? Get(ClaimsPrincipal user) => user.FindFirst(Type)?.Value;
}
