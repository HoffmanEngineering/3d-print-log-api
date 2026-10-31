using PrintLogApi.Exceptions;

namespace PrintLogApi.Services;

/// <summary>
/// Bounds how much memory image processing holds at once, across every upload path (print,
/// project, printer and material images, printer snapshots). Each upload reserves the bytes its
/// decoded pixels need before it decodes them, and waits in line while the budget is spent.
///
/// It exists because a decode is the one allocation an upload's own size limit does not bound:
/// a 10 MB PNG of flat colour can decode to 200 MB of pixels (50 MP at 4 bytes), and nothing
/// limited how many ran at once, so a handful of concurrent uploads from one account could run a
/// small App Service instance out of memory and restart it for everyone.
///
/// The budget is in bytes, not a count of uploads, so phone photos (about 50 MB decoded) still run
/// side by side while maximum-size images take turns. The line is first in, first out: a large
/// image at the head waits for room rather than being overtaken forever by small ones. A charge
/// larger than the whole budget is clamped to it, so such an image runs alone instead of never.
/// A wait longer than <see cref="MaxWait"/> gives up with <see cref="ImageProcessingBusyException"/>
/// (503 with <c>Retry-After</c>), which frees the request instead of queueing it indefinitely.
///
/// In process, like every other budget here: more than one instance multiplies it.
/// </summary>
public sealed class ImageMemoryGate
{
    /// <summary>
    /// 384 MiB, sized for the B1 plan (1.75 GB). That holds one maximum-size image (50 MP, rotated
    /// or not) or about seven phone photos at once, and leaves the rest for the app's own working
    /// set and the 8 MiB cache. Raise <c>ImageProcessing:MemoryBudgetMb</c> with the plan.
    /// </summary>
    public const long DefaultBudgetBytes = 384L * 1024 * 1024;

    /// <summary>Long enough to outlast a few maximum-size images ahead in line, short of a client's timeout.</summary>
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(20);

    private readonly object _lock = new();
    private readonly LinkedList<Waiter> _queue = new();
    private long _available;

    public ImageMemoryGate(long budgetBytes, TimeSpan maxWait)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);
        BudgetBytes = budgetBytes;
        MaxWait = maxWait;
        _available = budgetBytes;
    }

    public long BudgetBytes { get; }

    public TimeSpan MaxWait { get; }

    /// <summary>The bytes not reserved right now. For tests and diagnostics.</summary>
    public long AvailableBytes
    {
        get
        {
            lock (_lock)
            {
                return _available;
            }
        }
    }

    /// <summary>
    /// Reserves <paramref name="bytes"/> (clamped to the budget) until the returned lease is
    /// disposed. Throws <see cref="ImageProcessingBusyException"/> after <see cref="MaxWait"/>, and
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> fires first.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(long bytes, CancellationToken ct)
    {
        var charge = Math.Clamp(bytes, 1, BudgetBytes);
        Waiter waiter;
        LinkedListNode<Waiter> node;
        lock (_lock)
        {
            if (_queue.Count == 0 && _available >= charge)
            {
                _available -= charge;
                return new Lease(this, charge);
            }

            waiter = new Waiter(charge);
            node = _queue.AddLast(waiter);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(MaxWait);
        try
        {
            await waiter.Granted.Task.WaitAsync(timeout.Token);
            return new Lease(this, charge);
        }
        catch (OperationCanceledException)
        {
            bool granted;
            lock (_lock)
            {
                granted = waiter.Granted.Task.IsCompleted;
                if (!granted)
                {
                    _queue.Remove(node);

                    // Leaving the head of the line can let the next ones in.
                    Drain();
                }
            }

            if (granted)
            {
                // Granted in the same instant the wait gave up: hand the bytes back.
                Release(charge);
            }

            ct.ThrowIfCancellationRequested();
            throw new ImageProcessingBusyException();
        }
    }

    private void Release(long charge)
    {
        lock (_lock)
        {
            _available += charge;
            Drain();
        }
    }

    /// <summary>Grants waiters from the head of the line while they fit. Called under the lock.</summary>
    private void Drain()
    {
        while (_queue.First is { } first && first.Value.Charge <= _available)
        {
            _available -= first.Value.Charge;
            _queue.RemoveFirst();
            first.Value.Granted.TrySetResult();
        }
    }

    private sealed class Waiter(long charge)
    {
        public long Charge { get; } = charge;

        public TaskCompletionSource Granted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Lease(ImageMemoryGate gate, long charge) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release(charge);
            }
        }
    }
}
