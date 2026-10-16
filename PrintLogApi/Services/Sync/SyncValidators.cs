using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.Net.Http.Headers;

namespace PrintLogApi.Services.Sync;

/// <summary>
/// The opaque version and the entity tags the sync endpoints (#234) hand out and compare.
/// </summary>
public static class SyncValidators
{
    /// <summary>
    /// The caller-facing version: 32 hex digits of SHA-256 over the user id and the cache version
    /// GUID. Hashed so a response never carries cache-key material, and salted with the user id so
    /// two users can never share a value.
    /// </summary>
    public static string Version(long userId, string cacheVersion) =>
        Hash($"sync-version:{userId}:{cacheVersion}");

    /// <summary>32 lower-case hex digits of SHA-256 over <paramref name="value"/>.</summary>
    public static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];

    /// <summary>
    /// True when the request's <c>If-None-Match</c> matches <paramref name="etag"/> under the weak
    /// comparison RFC 9110 prescribes for it, so <c>W/</c>, lists and <c>*</c> all work.
    /// </summary>
    public static bool IfNoneMatchMatches(RequestHeaders headers, EntityTagHeaderValue etag) =>
        headers.IfNoneMatch is { Count: > 0 } candidates
        && candidates.Any(c => c.Equals(EntityTagHeaderValue.Any) || c.Compare(etag, useStrongComparison: false));
}
