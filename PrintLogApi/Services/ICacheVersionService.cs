namespace PrintLogApi.Services;

/// <summary>
/// Service for managing cache versions per user to enable efficient cache invalidation.
/// </summary>
public interface ICacheVersionService
{
    /// <summary>
    /// Gets the current cache version for a user.
    /// </summary>
    /// <param name="userId">The user ID to get the cache version for.</param>
    /// <returns>The current cache version string.</returns>
    string GetUserCacheVersion(long userId);

    /// <summary>
    /// Invalidates all cached data for a user by generating a new cache version.
    /// </summary>
    /// <param name="userId">The user ID to invalidate the cache for.</param>
    void InvalidateUserCache(long userId);
}
