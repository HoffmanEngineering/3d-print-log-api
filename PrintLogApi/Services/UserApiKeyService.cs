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

        public UserApiKeyService(PrintLogContext context, IMapper mapper, TelemetryClient telemetry, INotificationService notificationService)
        {
            _context = context;
            _mapper = mapper;
            _telemetry = telemetry;
            _notificationService = notificationService;
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

            // Cannot delete a key that isn't for your user.
            if (existingKey.UserId != userId)
            {
                throw new UserCannotAccessApiKeyException();
            }

            existingKey.IsDeleted = true;

            await _context.SaveChangesAsync();

            // Send security notification
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

            // Send security notification
            await _notificationService.CreateApiKeyCreatedNotification(userId, description);

            return response;

        }

        public async Task<long> GetUserIdByApiKey(string publicKey)
        {
            var hashedKey = GetSHA256Hash(publicKey);

            var userId = await _context.UserApiKeys.Where(u => u.HashedKey == hashedKey && u.IsDeleted == false).Select(u => u.UserId).SingleOrDefaultAsync();

            if (userId == default)
            {
                throw new ApiKeyIsNotValidException();
            }

            return userId;
        }

        /// <summary>
        /// Updates the LastUsed value for an api key by setting the date to now (UTC).
        /// </summary>
        /// <param name="publicKey"></param>
        /// <returns></returns>
        /// <exception cref="ApiKeyIsNotValidException"></exception>
        public async Task UpdateApiKeyLastUsed(string publicKey)
        {
            var hashedKey = GetSHA256Hash(publicKey);

            var apiKey = await _context.UserApiKeys.Where(u => u.HashedKey == hashedKey && u.IsDeleted == false).SingleOrDefaultAsync();

            if (apiKey == default)
            {
                throw new ApiKeyIsNotValidException();
            }

            apiKey.LastUsed = DateTimeOffset.UtcNow;

            await _context.SaveChangesAsync();
        }

        private string GetSHA256Hash(string publicKey)
        {
            using (SHA256 ShaHashFunction = SHA256.Create())
            {
                byte[] hashedBytes = ShaHashFunction.ComputeHash(Encoding.UTF8.GetBytes(publicKey));
                return Convert.ToBase64String(hashedBytes);
            };
        }

        private Guid CreateCryptographicallySecureGuid()
        {
            var bytes = RandomNumberGenerator.GetBytes(16);
            return new Guid(bytes);
        }
    }
}
