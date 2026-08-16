using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PrintLogApi.Models.DTOs.UserApiKeys;

namespace PrintLogApi.Services
{
    public interface IUserApiKeyService
    {
        Task DeactivateApiKey(Guid keyId, long userId);
        Task<NewUserApiKeyDto> GenerateNewApiKey(long userId, string? description);
        Task<List<UserApiKeyDto>> GetApiKeySummaryForUser(long userId);
        Task<long> GetUserIdByApiKey(string? publicKey);
        Task UpdateApiKeyLastUsed(string? publicKey);
    }
}
