using System.Security.Claims;
using PrintLogApi.Authentication;
using PrintLogApi.Models;
using PrintLogApi.Users;
using Xunit;

namespace PrintLogApi.IntegrationTests.Authentication;

public class ClaimsTransformerTests
{
    private static ClaimsPrincipal CreatePrincipal(string authUserId)
    {
        var claims = new[] { new Claim(ClaimTypes.Upn, authUserId) };
        var identity = new ClaimsIdentity(claims, "TestScheme");
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// ClaimsTransformer resolves IUserService from CachedComputation's scope rather than
    /// holding an injected one, so the fake has to be reachable through DI. Registered as a
    /// singleton so the instance the test asserts against is the one the factory used.
    /// </summary>
    private static ClaimsTransformer CreateTransformer(FakeUserService userService)
    {
        var (cache, computation) = TestHybridCache.Create(s => s.AddSingleton<IUserService>(userService));
        return new ClaimsTransformer(cache, computation);
    }

    [Fact]
    public async Task TransformAsync_ExistingUser_AddsCorrectNameIdentifierClaim()
    {
        var userService = new FakeUserService { ReturnUserId = 42L };
        var transformer = CreateTransformer(userService);

        var result = await transformer.TransformAsync(CreatePrincipal("auth|existing"));

        var claim = ((ClaimsIdentity)result.Identity!).FindFirst(ClaimTypes.NameIdentifier);
        Assert.NotNull(claim);
        Assert.Equal("42", claim.Value);
    }

    [Fact]
    public async Task TransformAsync_ExistingUser_DoesNotHitDatabaseOnSecondRequest()
    {
        var userService = new FakeUserService { ReturnUserId = 42L };
        var transformer = CreateTransformer(userService);

        await transformer.TransformAsync(CreatePrincipal("auth|existing"));
        await transformer.TransformAsync(CreatePrincipal("auth|existing"));

        Assert.Equal(1, userService.GetLocalUserIdCallCount);
    }

    [Fact]
    public async Task TransformAsync_NewUser_CreatesUserAndAddsCorrectNameIdentifierClaim()
    {
        var userService = new FakeUserService { ReturnUserId = 0L, NewUserId = 99L };
        var transformer = CreateTransformer(userService);

        var result = await transformer.TransformAsync(CreatePrincipal("auth|new"));

        var claim = ((ClaimsIdentity)result.Identity!).FindFirst(ClaimTypes.NameIdentifier);
        Assert.NotNull(claim);
        Assert.Equal("99", claim.Value);
    }

    [Fact]
    public async Task TransformAsync_NewUser_DoesNotHitDatabaseOnSecondRequest()
    {
        var userService = new FakeUserService { ReturnUserId = 0L, NewUserId = 99L };
        var transformer = CreateTransformer(userService);

        await transformer.TransformAsync(CreatePrincipal("auth|new"));
        await transformer.TransformAsync(CreatePrincipal("auth|new"));

        Assert.Equal(1, userService.GetLocalUserIdCallCount);
        Assert.Equal(1, userService.CreateUserCallCount);
    }

    /// <summary>
    /// The stampede guarantee #68 was opened for, asserted where it matters most: this miss path
    /// CREATES a user, so a stampede here is not just wasted work.
    ///
    /// The delay is what makes the test meaningful rather than incidental — without it each call
    /// would complete before the next began and the assertion would pass under a plain
    /// get/compute/set too. Holding every caller inside the factory window forces genuinely
    /// concurrent misses on one cold key, which is the case the old shape got wrong.
    /// </summary>
    [Fact]
    public async Task TransformAsync_ConcurrentColdCacheRequests_RunTheLookupOnce()
    {
        const int concurrentCallers = 32;

        var userService = new FakeUserService { ReturnUserId = 0L, NewUserId = 77L, Delay = TimeSpan.FromMilliseconds(150) };
        var (cache, computation) = TestHybridCache.Create(s => s.AddSingleton<IUserService>(userService));

        var principals = Enumerable.Range(0, concurrentCallers)
            .Select(_ => CreatePrincipal("auth|stampede"))
            .ToArray();

        // A fresh transformer per caller, matching the transient registration: nothing is
        // serialized by sharing an instance.
        var results = await Task.WhenAll(principals.Select(p =>
            Task.Run(() => new ClaimsTransformer(cache, computation).TransformAsync(p))));

        Assert.Equal(1, userService.GetLocalUserIdCallCount);
        Assert.Equal(1, userService.CreateUserCallCount);

        // Every caller must still get the answer, not just the one that won the race.
        Assert.All(results, r =>
            Assert.Equal("77", ((ClaimsIdentity)r.Identity!).FindFirst(ClaimTypes.NameIdentifier)!.Value));
    }

    private class FakeUserService : IUserService
    {
        private int _getLocalUserIdCallCount;
        private int _createUserCallCount;

        public long ReturnUserId { get; set; }
        public long NewUserId { get; set; }

        /// <summary>Holds callers inside the factory so concurrent misses genuinely overlap.</summary>
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        // Interlocked, not ++: the stampede test calls these from many threads at once, and a
        // torn increment would let a real stampede read back as a pass.
        public int GetLocalUserIdCallCount => Volatile.Read(ref _getLocalUserIdCallCount);
        public int CreateUserCallCount => Volatile.Read(ref _createUserCallCount);

        public async Task<long> GetLocalUserIdByAuthUserId(string authUserId)
        {
            Interlocked.Increment(ref _getLocalUserIdCallCount);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay);
            return ReturnUserId;
        }

        public Task<User> CreateUserFromAuthId(string authUserId)
        {
            Interlocked.Increment(ref _createUserCallCount);
            return Task.FromResult(new User { Id = NewUserId, OAuthUserId = authUserId });
        }

        public User GetLocalUserByAuthUserId(string authUserId) => throw new NotImplementedException();
        public Task MarkUserAsDeactivated(long userId) => throw new NotImplementedException();
        public Task ReactivateUser(long userId) => throw new NotImplementedException();
    }
}
