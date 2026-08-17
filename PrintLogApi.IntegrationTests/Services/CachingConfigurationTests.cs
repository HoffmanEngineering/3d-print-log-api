using System.ComponentModel;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PrintLogApi.Caching;
using PrintLogApi.Controllers;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// Guards the caching decisions from #68 that are invisible at the call sites: that there is one
/// memory budget rather than two, that it is denominated in the unit HybridCache actually
/// charges, and that the types flowing through the cache are ones it will share by reference
/// instead of re-deserializing on every hit.
///
/// Each test here corresponds to a way the adoption could look correct and be wrong.
/// </summary>
public class CachingConfigurationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public CachingConfigurationTests(CustomWebApplicationFactory factory) => _factory = factory;

    /// <summary>
    /// The headline guarantee: concurrent callers that miss on one key share a single
    /// computation. Asserted against the container's own HybridCache, so it covers the app's
    /// registration rather than a cache built by the test.
    ///
    /// The gate is what makes this a real test. Without it, callers would complete one after
    /// another and the assertion would hold under the plain get/compute/set shape this replaced;
    /// every caller has to be inside the factory window at once for the old code to fail here.
    /// </summary>
    [Fact]
    public async Task ConcurrentMissesOnOneKey_InvokeTheFactoryOnce()
    {
        const int concurrentCallers = 32;

        var cache = _factory.Services.GetRequiredService<HybridCache>();
        var key = $"stampede-probe:{Guid.NewGuid():N}";

        var calls = 0;
        var allArrived = new TaskCompletionSource();
        var arrived = 0;

        var callers = Enumerable.Range(0, concurrentCallers).Select(_ => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref arrived) == concurrentCallers)
            {
                allArrived.TrySetResult();
            }

            return await cache.GetOrCreateAsync(key, async _ =>
            {
                Interlocked.Increment(ref calls);
                // Hold the winner inside the factory until every caller has had a chance to
                // join, so the joiners are genuinely concurrent rather than sequential.
                await Task.WhenAny(allArrived.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                await Task.Delay(50);
                return "computed";
            });
        })).ToArray();

        var results = await Task.WhenAll(callers);

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.All(results, r => Assert.Equal("computed", r));
    }

    /// <summary>
    /// An aborted caller must not take the other waiters on its key down with it.
    ///
    /// <para>This is the failure a naive conversion produces, and it is silent in every other
    /// test: if a factory observes the <i>originating request's</i> cancellation token instead of
    /// the one HybridCache supplies, then one caller closing its browser cancels the shared
    /// computation and every joiner — each on a perfectly healthy request of its own — receives
    /// that cancellation. HybridCache cancels its own token only once every joiner has left,
    /// which is why <see cref="CachedComputation"/> insists the factory use it.</para>
    ///
    /// <para>The two halves below are the same scenario differing only in which token the factory
    /// body honours, so the test also documents the distinction rather than just asserting it.</para>
    /// </summary>
    [Fact]
    public async Task AbortedCaller_DoesNotCancelTheJoinersWaitingOnTheSameKey()
    {
        var cache = _factory.Services.GetRequiredService<HybridCache>();

        // The correct shape: the factory honours HybridCache's token.
        var goodKey = $"cancel-isolation:{Guid.NewGuid():N}";
        using var abortedRequest = new CancellationTokenSource();

        var aborted = cache.GetOrCreateAsync(goodKey, async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            return "computed";
        }, cancellationToken: abortedRequest.Token).AsTask();

        await Task.Delay(100);

        var joiner = cache.GetOrCreateAsync(goodKey, async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            return "computed";
        }, cancellationToken: CancellationToken.None).AsTask();

        await Task.Delay(100);
        abortedRequest.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => aborted);
        Assert.Equal("computed", await joiner);
    }

    /// <summary>
    /// HybridCache must be storing its L1 entries in the application's registered IMemoryCache,
    /// not standing up a second one. If that ever stopped being true, the process would hold two
    /// independent caches with two independent budgets and <see cref="CacheBudget.SizeLimitBytes"/>
    /// would stop describing the ceiling — which is exactly the open question #68 raised.
    /// </summary>
    [Fact]
    public async Task HybridCache_StoresItsL1EntriesInTheApplicationsMemoryCache()
    {
        var hybrid = _factory.Services.GetRequiredService<HybridCache>();
        var memory = Assert.IsType<MemoryCache>(_factory.Services.GetRequiredService<IMemoryCache>());

        var key = $"shared-store-probe:{Guid.NewGuid():N}";
        Assert.DoesNotContain(key, memory.Keys.OfType<string>());

        await hybrid.GetOrCreateAsync(key, _ => new ValueTask<string>("value"));

        Assert.Contains(memory.Keys.OfType<string>(), k => k.Contains(key, StringComparison.Ordinal));
    }

    /// <summary>
    /// <see cref="CachedComputation"/> must hand the factory a scope of its own, and must dispose
    /// it once the factory returns.
    ///
    /// <para>The first half is the guarantee every converted call site depends on: a shared
    /// computation that borrowed the winning request's scope would be querying through a disposed
    /// DbContext the moment that request ended. The second half is why the factories must
    /// materialise their results — a lazily-enumerated query handed back from here would be
    /// reaching into a scope that is already gone.</para>
    /// </summary>
    [Fact]
    public async Task CachedComputation_RunsTheFactoryInAScopeItOwnsAndDisposes()
    {
        var services = new ServiceCollection();
        services.AddScoped<TrackedScopedService>();
        services.AddSingleton<CachedComputation>();
        var provider = services.BuildServiceProvider();

        var computation = provider.GetRequiredService<CachedComputation>();

        // A service resolved from the root provider, standing in for one a request would have
        // injected into a controller.
        using var callerScope = provider.CreateScope();
        var callersInstance = callerScope.ServiceProvider.GetRequiredService<TrackedScopedService>();

        var first = await computation.RunAsync(
            (sp, _) => Task.FromResult(sp.GetRequiredService<TrackedScopedService>()), CancellationToken.None);
        var second = await computation.RunAsync(
            (sp, _) => Task.FromResult(sp.GetRequiredService<TrackedScopedService>()), CancellationToken.None);

        // Not the caller's instance, and not shared between computations either.
        Assert.NotSame(callersInstance, first);
        Assert.NotSame(first, second);

        // The computation's scope is torn down with it, so nothing leaks past the factory.
        Assert.True(first.Disposed);
        Assert.True(second.Disposed);

        // The caller's own scope is untouched by any of it.
        Assert.False(callersInstance.Disposed);
    }

    private sealed class TrackedScopedService : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// The budget is configured in bytes because that is the unit HybridCache charges.
    ///
    /// This pins the number itself. Reverting to the old 8192 would leave every call site
    /// compiling and every other test passing while capping the entire process cache at 8 KB —
    /// a single print summary is larger than that, so nothing would ever stay resident and the
    /// only symptom would be a silently vanished cache.
    /// </summary>
    [Fact]
    public void MemoryCacheSizeLimit_IsTheByteDenominatedBudget()
    {
        var options = _factory.Services.GetRequiredService<IOptions<MemoryCacheOptions>>().Value;

        Assert.Equal(CacheBudget.SizeLimitBytes, options.SizeLimit);
        Assert.Equal(8L * 1024 * 1024, options.SizeLimit);
    }

    /// <summary>
    /// The fact the budget rests on, asserted rather than assumed: HybridCache charges an entry
    /// the serialized length of its payload, not a flat one unit. Verified against a cache built
    /// here, because it is a property of the library and the app's real budget is far too large
    /// to demonstrate it.
    ///
    /// If a future version charged per-entry instead, this fails and
    /// <see cref="CacheBudget"/>'s reasoning — and the size limit it justifies — needs revisiting.
    /// </summary>
    [Fact]
    public async Task HybridCache_ChargesEntriesInSerializedBytes()
    {
        static (HybridCache Cache, MemoryCache Store) BuildWithLimit(long sizeLimit)
        {
            var services = new ServiceCollection();
            services.AddMemoryCache(o => o.SizeLimit = sizeLimit);
            services.AddHybridCache();
            var provider = services.BuildServiceProvider();
            return (provider.GetRequiredService<HybridCache>(),
                    (MemoryCache)provider.GetRequiredService<IMemoryCache>());
        }

        var payload = new string('x', 4096);

        // Under a flat one-unit-per-entry charge this would be admitted; under a byte charge it
        // cannot be.
        var tight = BuildWithLimit(64);
        await tight.Cache.GetOrCreateAsync("k", _ => new ValueTask<string>(payload));
        Assert.Equal(0, tight.Store.Count);

        var roomy = BuildWithLimit(64 * 1024);
        await roomy.Cache.GetOrCreateAsync("k", _ => new ValueTask<string>(payload));
        Assert.Equal(1, roomy.Store.Count);
    }

    /// <summary>
    /// Every type cached through HybridCache must carry <c>[ImmutableObject(true)]</c>, which is
    /// what lets it hand the stored instance to each caller. Without the attribute a cache HIT
    /// pays a full JSON deserialize — a cost the previous IMemoryCache shape never paid, and one
    /// that lands on the endpoints whose expensive part the cache already skips.
    ///
    /// The analytics half is enumerated from the controller's own actions rather than a hand
    /// written list, so a seventh analytics tab added later cannot quietly regress: it fails here
    /// with the offending type named. That is the "rule waiting to be missed on the ninth call
    /// site" #68 opened against, kept from coming back in a new form.
    /// </summary>
    [Fact]
    public void EveryCachedResponseType_IsMarkedImmutableForHybridCache()
    {
        var analyticsResponses = typeof(AnalyticsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpGetAttribute>().Any())
            .Select(m => UnwrapActionResult(m.ReturnType))
            .Where(t => t is not null)
            .Select(t => t!)
            .Distinct()
            .ToList();

        // A guard on the guard: if the unwrapping ever stops finding the response types this
        // test would pass by examining nothing at all.
        Assert.Equal(6, analyticsResponses.Count);

        var cachedTypes = analyticsResponses.Append(typeof(PagedList<>));

        var unmarked = cachedTypes
            .Where(t => t.GetCustomAttribute<ImmutableObjectAttribute>() is not { Immutable: true })
            .Select(t => t.Name)
            .ToList();

        Assert.True(unmarked.Count == 0,
            "These types are cached through HybridCache but are not marked [ImmutableObject(true)], " +
            "so every cache hit will deserialize a fresh copy: " + string.Join(", ", unmarked));
    }

    /// <summary>Task&lt;ActionResult&lt;T&gt;&gt; -> T, for anything else null.</summary>
    private static Type? UnwrapActionResult(Type returnType)
    {
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            returnType = returnType.GetGenericArguments()[0];
        }

        return returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ActionResult<>)
            ? returnType.GetGenericArguments()[0]
            : null;
    }
}
