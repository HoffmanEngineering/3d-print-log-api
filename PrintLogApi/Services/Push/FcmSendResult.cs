namespace PrintLogApi.Services.Push;

/// <summary>
/// Per-batch outcome. UnregisteredTokens contains ONLY tokens FCM reported as
/// UNREGISTERED — permanently gone, safe to delete. INVALID_ARGUMENT is deliberately
/// excluded: Firebase uses it for malformed payloads as well as bad tokens, so treating it
/// as a prune signal would let one bad message delete every device in the batch.
/// </summary>
public record FcmSendResult(
    IReadOnlyList<string> UnregisteredTokens,
    int SuccessCount,
    int FailureCount);
