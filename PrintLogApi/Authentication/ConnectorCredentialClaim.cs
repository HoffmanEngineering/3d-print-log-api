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
/// Today the only connector credential is a personal API key, added by <c>ApiKeyMiddleware</c>.
/// Scoped device tokens (#148) are meant to plug in by adding this claim with a <c>device:</c>
/// value from their own handler; the limiter needs no change for that.
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

    /// <summary>The credential the request authenticated with, or null for an interactive user.</summary>
    public static string? Get(ClaimsPrincipal user) => user.FindFirst(Type)?.Value;
}
