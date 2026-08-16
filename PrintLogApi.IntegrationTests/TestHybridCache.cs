using Microsoft.Extensions.Caching.Hybrid;

namespace PrintLogApi.IntegrationTests;

/// <summary>
/// Builds a real, isolated <see cref="HybridCache"/> for tests that construct a service directly
/// rather than going through the host.
///
/// <para>A real one rather than a fake: the behaviour these tests are asserting — that a second
/// call does not re-run the factory, that concurrent misses collapse into one — <i>is</i>
/// HybridCache's behaviour. A stub would assert only that the test double works. The type is
/// abstract with an internal implementation, so a ServiceCollection is the supported way to get
/// an instance.</para>
///
/// <para>Each call returns a fresh provider, so entries never leak between tests. The provider is
/// intentionally not disposed: it lives as long as the cache it owns, and the caches here are
/// tiny and collected with the test.</para>
/// </summary>
internal static class TestHybridCache
{
    public static HybridCache Create()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }
}
