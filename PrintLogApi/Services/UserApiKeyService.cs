using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.UserApiKeys;

namespace PrintLogApi.Services;

public class UserApiKeyService(
    PrintLogContext context,
    IMapper mapper,
    TelemetryClient telemetry,
    INotificationService notificationService,
    IMemoryCache cache) : IUserApiKeyService
{
    private static string UserIdCacheKey(string hashedKey) => $"apikey_userid:{hashedKey}";
    private static string LastUsedThrottleKey(string hashedKey) => $"apikey_lastused:{hashedKey}";

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

        cache.Remove(UserIdCacheKey(existingKey.HashedKey));
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

    public async Task<long> GetUserIdByApiKey(string? publicKey)
    {
        var hashedKey = GetSHA256Hash(publicKey);

        if (cache.TryGetValue(UserIdCacheKey(hashedKey), out long cachedUserId))
            return cachedUserId;

        var userId = await context.UserApiKeys
            .Where(u => u.HashedKey == hashedKey && u.IsDeleted == false)
            .Select(u => u.UserId)
            .SingleOrDefaultAsync();

        if (userId == default)
            throw new ApiKeyIsNotValidException();

        cache.Set(UserIdCacheKey(hashedKey), userId, new MemoryCacheEntryOptions()
            .SetSize(1)
            .SetSlidingExpiration(TimeSpan.FromHours(24))
            .SetAbsoluteExpiration(TimeSpan.FromDays(7)));

        return userId;
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

        cache.Set(LastUsedThrottleKey(hashedKey), true, new MemoryCacheEntryOptions()
            .SetSize(1)
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
