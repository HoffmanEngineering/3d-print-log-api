namespace PrintLogApi.Services.Push;

/// <summary>
/// Registered when push is disabled, unconfigured, or misconfigured. Push is an optional
/// transport: a bad Firebase credential must degrade this one feature, never prevent the
/// API from starting and take printing, login and the website down with it.
/// </summary>
public class NoOpFcmClient(ILogger<NoOpFcmClient> logger) : IFcmClient
{
    public Task<FcmSendResult> SendAsync(IReadOnlyList<FcmMessage> messages, CancellationToken ct)
    {
        logger.LogDebug("Push is disabled; dropping {Count} message(s).", messages.Count);
        return Task.FromResult(new FcmSendResult([], 0, messages.Count));
    }
}
