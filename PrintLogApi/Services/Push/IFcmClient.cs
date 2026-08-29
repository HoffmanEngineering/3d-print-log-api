namespace PrintLogApi.Services.Push;

/// <summary>One push to one device.</summary>
/// <remarks>
/// Data is IReadOnlyDictionary to match FirebaseAdmin's Message.Data exactly; an
/// IDictionary here would not implicitly convert at the assignment.
/// </remarks>
public record FcmMessage(
    string Token,
    string Title,
    string Body,
    IReadOnlyDictionary<string, string> Data,
    /// <summary>
    /// When the event being announced happened. Carried explicitly rather than defaulted at
    /// send time because AndroidNotification.EventTimestamp is non-nullable: an unset value
    /// ships as 0001-01-01, and Android renders the notification card's age from it.
    /// </summary>
    DateTimeOffset EventTime);

/// <summary>
/// The seam between dispatch and Google. Everything above this interface is testable
/// without network access; nothing below it runs in CI.
/// </summary>
public interface IFcmClient
{
    Task<FcmSendResult> SendAsync(IReadOnlyList<FcmMessage> messages, CancellationToken ct);
}
