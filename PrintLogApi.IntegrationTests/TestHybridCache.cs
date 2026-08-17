using Microsoft.Extensions.Caching.Hybrid;
using PrintLogApi.Caching;

namespace PrintLogApi.IntegrationTests;

/// <summary>
/// Builds a real, isolated <see cref="HybridCache"/> and <see cref="CachedComputation"/> for
/// tests that construct a service directly rather than going through the host.
///
/// <para>Real ones rather than fakes: the behaviour these tests assert — that a second call does
/// not re-run the factory, that concurrent misses collapse into one — <i>is</i> HybridCache's
/// behaviour, and a stub would only prove the stub works. Both types are resolved from a
/// ServiceCollection because HybridCache is abstract with an internal implementation, and because
/// CachedComputation needs a real scope factory to hand the factory a scope of its own.</para>
///
/// <para>Register whatever the factory under test resolves via <paramref name="configure"/>. Use
/// a singleton for a fake that a test asserts against afterwards — the computation creates a new
/// scope per miss, so a scoped registration would hand back a different instance than the test
/// holds.</para>
///
/// <para>Each call returns a fresh provider, so entries never leak between tests. The provider is
/// intentionally not disposed: it lives as long as the cache it owns, and these caches are tiny
/// and collected with the test.</para>
/// </summary>
internal static class TestHybridCache
{
    public static (HybridCache Cache, CachedComputation Computation) Create(
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        services.AddSingleton<CachedComputation>();
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<HybridCache>(),
                provider.GetRequiredService<CachedComputation>());
    }
}
