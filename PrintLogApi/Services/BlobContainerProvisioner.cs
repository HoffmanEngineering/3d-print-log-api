using System.Collections.Concurrent;

namespace PrintLogApi.Services;

public class BlobContainerProvisioner : IBlobContainerProvisioner
{
    // ConcurrentDictionary.GetOrAdd may invoke its value factory more than once.
    // Lazy<Task> keeps container creation exactly-once even under contention.
    private readonly ConcurrentDictionary<string, Lazy<Task>> _ensured = new();

    public async Task EnsureAsync(
        string containerName,
        Func<Task> create,
        CancellationToken ct = default)
    {
        var lazy = _ensured.GetOrAdd(
            containerName,
            _ => new Lazy<Task>(create, LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            // Cancelling one caller stops only that caller's wait. The shared ensure
            // continues for other callers and future uploads.
            await lazy.Value.WaitAsync(ct);
        }
        catch when (lazy.IsValueCreated && lazy.Value.IsFaulted)
        {
            // Never memoize a transient storage failure for the process lifetime.
            _ensured.TryRemove(new KeyValuePair<string, Lazy<Task>>(containerName, lazy));
            throw;
        }
    }
}
