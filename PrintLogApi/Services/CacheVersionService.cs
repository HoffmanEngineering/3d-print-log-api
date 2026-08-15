#nullable enable

using System;
using Microsoft.Extensions.Caching.Memory;

namespace PrintLogApi.Services
{
    /// <summary>
    /// Implementation of cache version service using in-memory cache.
    /// Manages per-user cache versions to enable efficient cache invalidation.
    /// </summary>
    public class CacheVersionService : ICacheVersionService
    {
        private readonly IMemoryCache _cache;
        private const string VERSION_PREFIX = "cache_version_user_";

        public CacheVersionService(IMemoryCache cache)
        {
            _cache = cache;
        }

        /// <inheritdoc />
        public string GetUserCacheVersion(long userId)
        {
            var key = $"{VERSION_PREFIX}{userId}";
            
            if (!_cache.TryGetValue(key, out string? version))
            {
                version = Guid.NewGuid().ToString("N");
                
                var cacheOptions = new MemoryCacheEntryOptions()
                    .SetSize(1) // Small size for version tracking
                    .SetSlidingExpiration(TimeSpan.FromHours(24))
                    .SetAbsoluteExpiration(TimeSpan.FromDays(7));
                
                _cache.Set(key, version, cacheOptions);
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
                .SetSize(1)
                .SetSlidingExpiration(TimeSpan.FromHours(24))
                .SetAbsoluteExpiration(TimeSpan.FromDays(7));
            
            _cache.Set(key, newVersion, cacheOptions);
        }
    }
}
