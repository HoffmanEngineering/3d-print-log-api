using PrintLogApi.Email;

namespace PrintLogApi.IntegrationTests.Email;

/// <summary>A scriptable <see cref="IEmailVerificationRefresher"/>: returns <see cref="Result"/> and counts calls.</summary>
public sealed class FakeVerificationRefresher : IEmailVerificationRefresher
{
    public bool Result { get; set; }

    public int Calls { get; private set; }

    /// <summary>The <c>force</c> flag of the most recent call; null before the first.</summary>
    public bool? LastForce { get; private set; }

    public Task<bool> RefreshAsync(long userId, CancellationToken ct, bool force = false)
    {
        Calls++;
        LastForce = force;
        return Task.FromResult(Result);
    }
}
