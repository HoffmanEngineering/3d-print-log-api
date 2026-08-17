namespace PrintLogApi.IntegrationTests.Analytics;

/// <summary>
/// A <see cref="TimeProvider"/> whose "now" is whatever a test last assigned, in either
/// direction.
///
/// Deliberately NOT <c>FakeTimeProvider</c> from Microsoft.Extensions.TimeProvider.Testing.
/// That type models a clock, so <c>SetUtcNow</c> throws on any value earlier than the current
/// one — which is correct for what it is, and wrong here: the provider is registered once for
/// a shared host, and the tests using it each choose an instant independently. With
/// FakeTimeProvider the second test to run would throw purely because the first one had moved
/// the clock forward, making the suite depend on xunit's method ordering. That is a worse
/// property than the one monotonicity buys, and none of these tests use timers, which is the
/// part of FakeTimeProvider a hand-rolled double would actually be reimplementing badly.
///
/// Everything not overridden falls through to the base class, which is fine: only
/// <see cref="GetUtcNow"/> is consulted by the code under test.
/// </summary>
public sealed class SettableTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _utcNow = start;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void SetUtcNow(DateTimeOffset value) => _utcNow = value;
}
