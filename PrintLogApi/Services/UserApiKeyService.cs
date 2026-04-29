using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.UserApiKeys;

namespace PrintLogApi.Services
{
    public class UserApiKeyService : IUserApiKeyService
    {
        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;
        private readonly TelemetryClient _telemetry;
        private readonly INotificationService _notificationService;
        private readonly IMemoryCache _cache;

        private static string UserIdCacheKey(string hashedKey) => $"apikey_userid:{hashedKey}";
        private static string LastUsedThrottleKey(string hashedKey) => $"apikey_lastused:{hashedKey}";

        public UserApiKeyService(PrintLogContext context, IMapper mapper, TelemetryClient telemetry, INotificationService notificationService, IMemoryCache cache)
        {
            _context = context;
            _mapper = mapper;
            _telemetry = telemetry;
            _notificationService = notificationService;
            _cache = cache;
        }

        public async Task<List<UserApiKeyDto>> GetApiKeySummaryForUser(long userId)
        {
            return await _context.UserApiKeys
                .Where(u => u.UserId == userId && u.IsDeleted == false)
                .OrderByDescending(u => u.CreatedDate)
                .ProjectTo<UserApiKeyDto>(_mapper.ConfigurationProvider)
                .AsNoTracking()
                .ToListAsync();
        }

        public async Task DeactivateApiKey(Guid keyId, long userId)
        {
            var existingKey = await _context.UserApiKeys.FindAsync(keyId);

            if (existingKey == null || existingKey.IsDeleted)
            {
                throw new DoesNotExistException();
            }

            if (existingKey.UserId != userId)
            {
                throw new UserCannotAccessApiKeyException();
            }

            existingKey.IsDeleted = true;

            await _context.SaveChangesAsync();

            _cache.Remove(UserIdCacheKey(existingKey.HashedKey));
            _cache.Remove(LastUsedThrottleKey(existingKey.HashedKey));

            await _notificationService.CreateApiKeyDeletedNotification(userId, existingKey.Description);
        }

        public async Task<NewUserApiKeyDto> GenerateNewApiKey(long userId, string description)
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

            _context.UserApiKeys.Add(entity);
            await _context.SaveChangesAsync();

            var response = new NewUserApiKeyDto()
            {
                Id = entity.Id,
                CreatedDate = entity.CreatedDate,
                Description = entity.Description,
                PublicKey = publicKey
            };

            _telemetry.TrackEvent("NewApiKeyGenerated");

            await _notificationService.CreateApiKeyCreatedNotification(userId, description);

            return response;
        }

        public async Task<long> GetUserIdByApiKey(string publicKey)
        {
            var hashedKey = GetSHA256Hash(publicKey);

            if (_cache.TryGetValue(UserIdCacheKey(hashedKey), out long cachedUserId))
                return cachedUserId;

            var userId = await _context.UserApiKeys
                .Where(u => u.HashedKey == hashedKey && u.IsDeleted == false)
                .Select(u => u.UserId)
                .SingleOrDefaultAsync();

            if (userId == default)
                throw new ApiKeyIsNotValidException();

            _cache.Set(UserIdCacheKey(hashedKey), userId, new MemoryCacheEntryOptions()
                .SetSize(1)
                .SetSlidingExpiration(TimeSpan.FromHours(24))
                .SetAbsoluteExpiration(TimeSpan.FromDays(7)));

            return userId;
        }

        public async Task UpdateApiKeyLastUsed(string publicKey)
        {
            var hashedKey = GetSHA256Hash(publicKey);

            if (_cache.TryGetValue(LastUsedThrottleKey(hashedKey), out _))
                return;

            var apiKey = await _context.UserApiKeys
                .Where(u => u.HashedKey == hashedKey && u.IsDeleted == false)
                .SingleOrDefaultAsync();

            if (apiKey == default)
                throw new ApiKeyIsNotValidException();

            apiKey.LastUsed = DateTimeOffset.UtcNow;
            await _context.SaveChangesAsync();

            _cache.Set(LastUsedThrottleKey(hashedKey), true, new MemoryCacheEntryOptions()
                .SetSize(1)
                .SetAbsoluteExpiration(TimeSpan.FromHours(1)));
        }

        private string GetSHA256Hash(string publicKey)
        {
            using var sha = SHA256.Create();
            byte[] hashedBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(publicKey));
            return Convert.ToBase64String(hashedBytes);
        }

        private Guid CreateCryptographicallySecureGuid()
        {
            var bytes = RandomNumberGenerator.GetBytes(16);
            return new Guid(bytes);
        }
    }
}
