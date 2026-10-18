using PrintLogApi.Email;

namespace PrintLogApi.IntegrationTests.Email;

/// <summary>A scriptable <see cref="IEmailVerificationRefresher"/>: returns <see cref="Result"/> and counts calls.</summary>
public sealed class FakeVerificationRefresher : IEmailVerificationRefresher
{
    public bool Result { get; set; }

    public int Calls { get; private set; }

    public Task<bool> RefreshAsync(long userId, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(Result);
    }
}
