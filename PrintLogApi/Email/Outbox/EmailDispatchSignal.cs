using System.Threading.Channels;

namespace PrintLogApi.Email.Outbox;

/// <summary>
/// Wakes <see cref="EmailDispatcherService"/> before its next poll. The dispatcher polls every
/// <see cref="EmailOptions.DispatchInterval"/> (two minutes), which is fine for engagement mail
/// but too slow for a security notice, so code that queues one wakes the dispatcher after its
/// save commits.
///
/// <para>Wakes coalesce: any number of them before the dispatcher looks is one extra tick, so a
/// burst cannot turn into a burst of database work. A wake is per process, like the dispatcher
/// itself; a row queued on another instance still goes out on that instance's next poll.</para>
/// </summary>
public sealed class EmailDispatchSignal
{
    private readonly Channel<bool> _wakes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
    });

    /// <summary>Asks for a dispatch tick now. Never blocks, never throws.</summary>
    public void Wake() => _wakes.Writer.TryWrite(true);

    /// <summary>Completes when a wake is pending, and takes it.</summary>
    public async Task WaitAsync(CancellationToken ct) => await _wakes.Reader.ReadAsync(ct);

    /// <summary>Takes a pending wake, if there is one.</summary>
    public bool TryConsume() => _wakes.Reader.TryRead(out _);
}
