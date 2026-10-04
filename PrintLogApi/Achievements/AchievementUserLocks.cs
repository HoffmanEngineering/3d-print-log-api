namespace PrintLogApi.Achievements;

/// <summary>
/// Serializes evaluation passes per user, so two passes never decide catch-up and
/// retroactivity from the same stale user row (e.g. <c>/me</c> racing a new print's pass at
/// launch). In-process, which is enough because the API runs as a single instance. Striped:
/// users sharing a stripe merely wait on each other, and a pass never takes a second stripe, so
/// there is nothing to clean up and no lock ordering to get wrong.
/// </summary>
public sealed class AchievementUserLocks
{
    private const int Stripes = 64;
    private readonly SemaphoreSlim[] _stripes = Enumerable.Range(0, Stripes).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public async Task<IDisposable> AcquireAsync(long userId, CancellationToken ct)
    {
        var stripe = _stripes[(int)((ulong)userId % Stripes)];
        await stripe.WaitAsync(ct);
        return new Releaser(stripe);
    }

    private sealed class Releaser(SemaphoreSlim stripe) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                stripe.Release();
            }
        }
    }
}
