using PrintLogApi.Email.Transport;

namespace PrintLogApi.IntegrationTests.Email;

/// <summary>
/// Records what would have been sent. Script a failure by enqueueing an exception: each send
/// dequeues one before recording, so a queued <see cref="EmailSendException"/> is what the
/// dispatcher sees for that call.
/// </summary>
public sealed class FakeEmailTransport : IEmailTransport
{
    private int _sequence;

    public List<EmailMessage> Sent { get; } = [];

    public Queue<Exception> Throws { get; } = new();

    /// <summary>The id the next successful send returns; generated when null.</summary>
    public string? NextMessageId { get; set; }

    public Task<string> SendAsync(EmailMessage message, CancellationToken ct)
    {
        lock (Sent)
        {
            if (Throws.TryDequeue(out var ex))
            {
                throw ex;
            }

            Sent.Add(message);
            var id = NextMessageId ?? $"fake-{Interlocked.Increment(ref _sequence)}";
            NextMessageId = null;
            return Task.FromResult(id);
        }
    }
}
