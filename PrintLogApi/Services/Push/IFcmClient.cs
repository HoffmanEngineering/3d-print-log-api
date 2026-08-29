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
    IReadOnlyDictionary<string, string> Data);

/// <summary>
/// The seam between dispatch and Google. Everything above this interface is testable
/// without network access; nothing below it runs in CI.
/// </summary>
public interface IFcmClient
{
    Task<FcmSendResult> SendAsync(IReadOnlyList<FcmMessage> messages, CancellationToken ct);
}
