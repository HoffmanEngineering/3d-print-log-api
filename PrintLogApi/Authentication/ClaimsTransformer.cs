using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using PrintLogApi.Users;

namespace PrintLogApi.Authentication
{
    public sealed class ClaimsTransformer : IClaimsTransformation
    {
        private readonly IUserService userService;
        private readonly IMemoryCache cache;

        public ClaimsTransformer(IUserService userService, IMemoryCache cache)
        {
            this.userService = userService;
            this.cache = cache;
        }

        public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            // A principal reaching claims transformation always carries an identity. Casting a
            // null Identity would succeed and then throw on .Claims below, so the null-forgive
            // preserves the existing behaviour exactly.
            var existingClaimsIdentity = (ClaimsIdentity)principal.Identity!;

            var authUserId = existingClaimsIdentity.Claims
                .FirstOrDefault(c => c.Type == ClaimTypes.Upn)?.Value;

            // Fail closed: a token with no subject must not resolve to (or create) a user.
            if (string.IsNullOrWhiteSpace(authUserId))
            {
                return principal;
            }

            var cacheKey = $"user_id:{authUserId}";
            if (!cache.TryGetValue(cacheKey, out long localUserId))
            {
                localUserId = await userService.GetLocalUserIdByAuthUserId(authUserId);

                if (localUserId == 0)
                {
                    var newUser = await userService.CreateUserFromAuthId(authUserId);
                    localUserId = newUser.Id;
                }

                cache.Set(cacheKey, localUserId, new MemoryCacheEntryOptions()
                    .SetSize(1)
                    .SetSlidingExpiration(TimeSpan.FromHours(24))
                    .SetAbsoluteExpiration(TimeSpan.FromDays(7)));
            }

            existingClaimsIdentity.AddClaim(new Claim(ClaimTypes.NameIdentifier, localUserId.ToString()));

            return principal;
        }
    }
}
