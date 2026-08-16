using Microsoft.Extensions.Caching.Memory;
using PrintLogApi.Caching;

namespace PrintLogApi.Services;

/// <summary>
/// Implementation of cache version service using in-memory cache.
/// Manages per-user cache versions to enable efficient cache invalidation.
///
/// <para>Deliberately still on IMemoryCache after #68, on three counts. It is the <i>source</i>
/// of the version GUIDs that every HybridCache key is built from, so putting it behind the
/// cache it feeds inverts the dependency for no gain. Its contract is synchronous and
/// HybridCache is async-only, which would push a Task up through every caller that composes a
/// cache key. And there is nothing to deduplicate: the "computation" on a miss is
/// Guid.NewGuid(), so a stampede costs a few wasted GUIDs, and two racing callers each minting
/// their own is harmless — a version nobody has cached under yet invalidates nothing.</para>
/// </summary>
public class CacheVersionService(IMemoryCache cache) : ICacheVersionService
{
    private const string VERSION_PREFIX = "cache_version_user_";

    /// <inheritdoc />
    public string GetUserCacheVersion(long userId)
    {
        var key = $"{VERSION_PREFIX}{userId}";

        if (!cache.TryGetValue(key, out string? version))
        {
            version = Guid.NewGuid().ToString("N");

            var cacheOptions = new MemoryCacheEntryOptions()
                .SetSize(CacheBudget.SmallEntryBytes)
                .SetSlidingExpiration(TimeSpan.FromHours(24))
                .SetAbsoluteExpiration(TimeSpan.FromDays(7));

            cache.Set(key, version, cacheOptions);
        }

        // Null-forgiven: nothing ever caches a null under this key - the miss branch above
        // assigns one, and InvalidateUserCache only ever stores a fresh GUID.
        return version!;
    }

    /// <inheritdoc />
    public void InvalidateUserCache(long userId)
    {
        var key = $"{VERSION_PREFIX}{userId}";
        var newVersion = Guid.NewGuid().ToString("N");

        var cacheOptions = new MemoryCacheEntryOptions()
            .SetSize(CacheBudget.SmallEntryBytes)
            .SetSlidingExpiration(TimeSpan.FromHours(24))
            .SetAbsoluteExpiration(TimeSpan.FromDays(7));

        cache.Set(key, newVersion, cacheOptions);
    }
}
