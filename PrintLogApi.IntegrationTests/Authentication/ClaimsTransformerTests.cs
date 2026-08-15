using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using PrintLogApi.Authentication;
using PrintLogApi.Models;
using PrintLogApi.Users;
using Xunit;

namespace PrintLogApi.IntegrationTests.Authentication
{
    public class ClaimsTransformerTests
    {
        private static ClaimsPrincipal CreatePrincipal(string authUserId)
        {
            var claims = new[] { new Claim(ClaimTypes.Upn, authUserId) };
            var identity = new ClaimsIdentity(claims, "TestScheme");
            return new ClaimsPrincipal(identity);
        }

        private static IMemoryCache CreateCache() =>
            new MemoryCache(new MemoryCacheOptions());

        [Fact]
        public async Task TransformAsync_ExistingUser_AddsCorrectNameIdentifierClaim()
        {
            var userService = new FakeUserService { ReturnUserId = 42L };
            var transformer = new ClaimsTransformer(userService, CreateCache());

            var result = await transformer.TransformAsync(CreatePrincipal("auth|existing"));

            var claim = ((ClaimsIdentity)result.Identity!).FindFirst(ClaimTypes.NameIdentifier);
            Assert.NotNull(claim);
            Assert.Equal("42", claim.Value);
        }

        [Fact]
        public async Task TransformAsync_ExistingUser_DoesNotHitDatabaseOnSecondRequest()
        {
            var userService = new FakeUserService { ReturnUserId = 42L };
            var transformer = new ClaimsTransformer(userService, CreateCache());

            await transformer.TransformAsync(CreatePrincipal("auth|existing"));
            await transformer.TransformAsync(CreatePrincipal("auth|existing"));

            Assert.Equal(1, userService.GetLocalUserIdCallCount);
        }

        [Fact]
        public async Task TransformAsync_NewUser_CreatesUserAndAddsCorrectNameIdentifierClaim()
        {
            var userService = new FakeUserService { ReturnUserId = 0L, NewUserId = 99L };
            var transformer = new ClaimsTransformer(userService, CreateCache());

            var result = await transformer.TransformAsync(CreatePrincipal("auth|new"));

            var claim = ((ClaimsIdentity)result.Identity!).FindFirst(ClaimTypes.NameIdentifier);
            Assert.NotNull(claim);
            Assert.Equal("99", claim.Value);
        }

        [Fact]
        public async Task TransformAsync_NewUser_DoesNotHitDatabaseOnSecondRequest()
        {
            var userService = new FakeUserService { ReturnUserId = 0L, NewUserId = 99L };
            var transformer = new ClaimsTransformer(userService, CreateCache());

            await transformer.TransformAsync(CreatePrincipal("auth|new"));
            await transformer.TransformAsync(CreatePrincipal("auth|new"));

            Assert.Equal(1, userService.GetLocalUserIdCallCount);
            Assert.Equal(1, userService.CreateUserCallCount);
        }

        private class FakeUserService : IUserService
        {
            public long ReturnUserId { get; set; }
            public long NewUserId { get; set; }
            public int GetLocalUserIdCallCount { get; private set; }
            public int CreateUserCallCount { get; private set; }

            public Task<long> GetLocalUserIdByAuthUserId(string authUserId)
            {
                GetLocalUserIdCallCount++;
                return Task.FromResult(ReturnUserId);
            }

            public Task<User> CreateUserFromAuthId(string authUserId)
            {
                CreateUserCallCount++;
                return Task.FromResult(new User { Id = NewUserId, OAuthUserId = authUserId });
            }

            public User GetLocalUserByAuthUserId(string authUserId) => throw new NotImplementedException();
            public Task MarkUserAsDeactivated(long userId) => throw new NotImplementedException();
            public Task ReactivateUser(long userId) => throw new NotImplementedException();
        }
    }
}
