using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.UserApiKeys;

namespace PrintLogApi.Services
{
    public class UserApiKeyService : IUserApiKeyService
    {

        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;
        private readonly TelemetryClient _telemetry;

        public UserApiKeyService(PrintLogContext context, IMapper mapper, TelemetryClient telemetry)
        {
            _context = context;
            _mapper = mapper;
            _telemetry = telemetry;
        }

        public async Task<List<UserApiKeyDto>> GetApiKeySummaryForUser(long userId)
        {
            return await _context.UserApiKeys
                .Where(u => u.UserId == userId && u.IsDeleted == false)
                .ProjectTo<UserApiKeyDto>(_mapper.ConfigurationProvider)
                .AsNoTracking()
                .ToListAsync();
        }

        public async Task<NewUserApiKeyDto> GenerateNewApiKey(long userId, string description)
        {
            var publicKey = CreateCryptographicallySecureGuid().ToString().ToUpper().Replace("-", "");

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
            using (var provider = new RNGCryptoServiceProvider())
            {
                var bytes = new byte[16];
                provider.GetBytes(bytes);

                return new Guid(bytes);
            }
        }
    }
}
