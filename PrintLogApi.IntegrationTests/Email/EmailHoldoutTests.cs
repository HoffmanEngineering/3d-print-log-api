using PrintLogApi.Email;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email;

public class EmailHoldoutTests
{
    [Fact]
    public void ZeroPercent_NeverHoldsOut()
        => Assert.DoesNotContain(Enumerable.Range(1, 1000), id => EmailHoldout.IsHoldout(id, 0));

    [Fact]
    public void HundredPercent_AlwaysHoldsOut()
        => Assert.All(Enumerable.Range(1, 1000), id => Assert.True(EmailHoldout.IsHoldout(id, 100)));

    [Fact]
    public void TenPercent_HoldsOutAboutTenPercent()
    {
        var held = Enumerable.Range(1, 10_000).Count(id => EmailHoldout.IsHoldout(id, 10));
        Assert.InRange(held, 900, 1100);
    }

    [Fact]
    public void Assignment_IsStable()
        => Assert.All(Enumerable.Range(1, 200), id => Assert.Equal(EmailHoldout.IsHoldout(id, 10), EmailHoldout.IsHoldout(id, 10)));

    // Pins the formula (first 4 bytes of SHA-256("email-holdout-v1:" + id), big-endian, mod 100) so
    // an accidental change — which would reshuffle every user between arms mid-experiment — fails
    // loudly. Expected values were computed independently with Python's hashlib.
    [Fact]
    public void Assignment_IsPinned()
    {
        var firstHeld = Enumerable.Range(1, 200).Where(id => EmailHoldout.IsHoldout(id, 10)).Take(5).ToArray();
        Assert.Equal([24, 25, 27, 46, 48], firstHeld);
    }
}
