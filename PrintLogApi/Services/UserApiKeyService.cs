using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using PrintLogApi.Caching;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.UserApiKeys;

namespace PrintLogApi.Services;

public class UserApiKeyService(
    PrintLogContext context,
    IMapper mapper,
    TelemetryClient telemetry,
    INotificationService notificationService,
    IMemoryCache cache,
    HybridCache hybridCache) : IUserApiKeyService
{
    private static string UserIdCacheKey(string hashedKey) => $"apikey_userid:{hashedKey}";
    private static string LastUsedThrottleKey(string hashedKey) => $"apikey_lastused:{hashedKey}";

    private static readonly HybridCacheEntryOptions ApiKeyCacheOptions = new()
    {
        Expiration = TimeSpan.FromHours(24),
        LocalCacheExpiration = TimeSpan.FromHours(24),
    };

    public async Task<List<UserApiKeyDto>> GetApiKeySummaryForUser(long userId)
    {
        return await context.UserApiKeys
            .Where(u => u.UserId == userId && u.IsDeleted == false)
            .OrderByDescending(u => u.CreatedDate)
            .ProjectTo<UserApiKeyDto>(mapper.ConfigurationProvider)
            .AsNoTracking()
            .ToListAsync();
    }

    public async Task DeactivateApiKey(Guid keyId, long userId)
    {
        var existingKey = await context.UserApiKeys.FindAsync(keyId);

        if (existingKey == null || existingKey.IsDeleted)
        {
            throw new DoesNotExistException();
        }

        if (existingKey.UserId != userId)
        {
            throw new UserCannotAccessApiKeyException();
        }

        existingKey.IsDeleted = true;

        await context.SaveChangesAsync();

        // Two caches, because these two entries are different kinds of thing: the owner lookup
        // is a compute-on-miss cache and lives in HybridCache, while the last-used throttle is a
        // flag whose value IS its existence and stays on IMemoryCache. Revocation must clear
        // both, and removing from the wrong one would silently leave a revoked key working for
        // up to a day.
        await hybridCache.RemoveAsync(UserIdCacheKey(existingKey.HashedKey));
        cache.Remove(LastUsedThrottleKey(existingKey.HashedKey));

        await notificationService.CreateApiKeyDeletedNotification(userId, existingKey.Description);
    }

    public async Task<NewUserApiKeyDto> GenerateNewApiKey(long userId, string? description)
    {
        var publicKey = CreateCryptographicallySecureGuid().ToString().ToUpper(CultureInfo.InvariantCulture).Replace("-", "");

        var hashedKey = GetSHA256Hash(publicKey);

        var entity = new UserApiKey()
        {
            HashedKey = hashedKey,
            HashAlgorithm = "SHA256",
            Description = description,
            UserId = userId,
            CreatedById = userId,
            UpdatedById = userId,
            IsDeleted = false,
        };

        context.UserApiKeys.Add(entity);
        await context.SaveChangesAsync();

        var response = new NewUserApiKeyDto()
        {
            Id = entity.Id,
            CreatedDate = entity.CreatedDate,
            Description = entity.Description,
            PublicKey = publicKey
        };

        telemetry.TrackEvent("NewApiKeyGenerated");

        await notificationService.CreateApiKeyCreatedNotification(userId, description);

        return response;
    }

    /// <summary>
    /// Resolves an API key to its owner, on the hot path of every API-key-authenticated request.
    ///
    /// <para>The unknown-key branch still throws rather than caching a sentinel, which keeps the
    /// existing property that an invalid key is never cached — so re-issuing a key is not
    /// blocked by a negative entry. HybridCache stores nothing when the factory throws, and it
    /// propagates that exception to every caller waiting on the same key, so a burst of requests
    /// bearing one bad key now costs a single query between them instead of one each.</para>
    ///
    /// <para>The old entry combined a 24h sliding window with a 7-day absolute cap. HybridCache
    /// has absolute expiry only, so this is a flat 24 hours: at worst one extra indexed lookup
    /// per key per day, against a revoked key no longer being able to stay resident for a week
    /// of continuous use. Revocation does not wait for expiry in any case — see
    /// DeactivateApiKey, which removes the entry explicitly.</para>
    /// </summary>
    public async Task<long> GetUserIdByApiKey(string? publicKey)
    {
        var hashedKey = GetSHA256Hash(publicKey);

        return await hybridCache.GetOrCreateAsync(
            UserIdCacheKey(hashedKey),
            (context, hashedKey),
            static async (state, ct) =>
            {
                var userId = await state.context.UserApiKeys
                    .Where(u => u.HashedKey == state.hashedKey && u.IsDeleted == false)
                    .Select(u => u.UserId)
                    .SingleOrDefaultAsync(ct);

                if (userId == default)
                    throw new ApiKeyIsNotValidException();

                return userId;
            },
            ApiKeyCacheOptions);
    }

    public async Task UpdateApiKeyLastUsed(string? publicKey)
    {
        var hashedKey = GetSHA256Hash(publicKey);

        if (cache.TryGetValue(LastUsedThrottleKey(hashedKey), out _))
            return;

        // ExecuteUpdateAsync issues a single UPDATE and skips loading and tracking the entity
        // entirely — this runs on the API-key request path, and the row is only ever read here
        // to stamp one column. Matches NotificationService and UserDeletionService, which
        // already use it. A row count of zero means no live key matched, which is the same
        // condition the previous null check caught.
        var rowsUpdated = await context.UserApiKeys
            .Where(u => u.HashedKey == hashedKey && u.IsDeleted == false)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(u => u.LastUsed, DateTimeOffset.UtcNow));

        if (rowsUpdated == 0)
            throw new ApiKeyIsNotValidException();

        // Deliberately still IMemoryCache, not HybridCache: this is a throttle flag, not a
        // cached computation. Its value carries no information — its presence is the whole
        // signal — so there is no miss to deduplicate and GetOrCreateAsync would only obscure
        // that. Same category as ApiKeyMiddleware's failed-attempt counter.
        cache.Set(LastUsedThrottleKey(hashedKey), true, new MemoryCacheEntryOptions()
            .SetSize(CacheBudget.SmallEntryBytes)
            .SetAbsoluteExpiration(TimeSpan.FromHours(1)));
    }

    /// <summary>
    /// Hashes an API key for lookup and storage.
    ///
    /// Uses the one-shot static SHA256.HashData rather than SHA256.Create(): this runs on every
    /// API-key-authenticated request, and the instance form allocated and disposed a hash object
    /// each time. Matches how the rest of the codebase already hashes (McpUserContext,
    /// McpRequestFingerprint). The output encoding is unchanged, so stored hashes still match.
    /// </summary>
    private static string GetSHA256Hash(string? publicKey)
    {
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(publicKey!)));
    }

    private Guid CreateCryptographicallySecureGuid()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return new Guid(bytes);
    }
}
