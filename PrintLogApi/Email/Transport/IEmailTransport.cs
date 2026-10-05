namespace PrintLogApi.Email.Transport;

public interface IEmailTransport
{
    /// <summary>Sends one message and returns the provider's message id.</summary>
    /// <exception cref="EmailSendException">Every failure, classified for the dispatcher.</exception>
    Task<string> SendAsync(EmailMessage message, CancellationToken ct);
}

/// <param name="Headers">Extra headers (the RFC 8058 pair on campaign mail).</param>
/// <param name="Campaign">Tagged on the message so delivery events can be attributed.</param>
/// <param name="OutboxId">Tagged on the message so an ambiguous send can be reconciled from a later Delivery event.</param>
public sealed record EmailMessage(
    string ToAddress,
    string Subject,
    string Html,
    string Text,
    IReadOnlyDictionary<string, string> Headers,
    string Campaign,
    long OutboxId);

/// <summary>What a failed send means for the outbox row (spec §6.3).</summary>
public enum EmailSendFailureKind
{
    /// <summary>The provider refused for rate reasons; the message was not sent. Retry with backoff.</summary>
    Throttled,

    /// <summary>No connection was made; the message cannot have been sent. Retry with backoff.</summary>
    NotConnected,

    /// <summary>The provider declined this message. Retrying would fail the same way.</summary>
    Rejected,

    /// <summary>Sending is paused or suspended account-wide. Stop the tick; the row is not at fault.</summary>
    AccountPaused,

    /// <summary>The request may have reached the provider. Never retried: a duplicate costs more than a miss.</summary>
    Ambiguous,
}

public sealed class EmailSendException(EmailSendFailureKind kind, Exception inner)
    : Exception($"Email send failed ({kind}): {inner.Message}", inner)
{
    public EmailSendFailureKind Kind { get; } = kind;
}
