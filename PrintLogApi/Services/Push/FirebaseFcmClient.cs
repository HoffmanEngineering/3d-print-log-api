using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;

namespace PrintLogApi.Services.Push;

public class FirebaseFcmClient : IFcmClient
{
    private readonly PushOptions _options;
    private readonly ILogger<FirebaseFcmClient> _logger;
    private readonly FirebaseMessaging _messaging;

    public FirebaseFcmClient(IOptions<PushOptions> options, ILogger<FirebaseFcmClient> logger)
    {
        _options = options.Value;
        _logger = logger;

        var app = FirebaseApp.DefaultInstance ?? FirebaseApp.Create(new AppOptions
        {
            Credential = GoogleCredential.FromJson(_options.ServiceAccountJson)
        });

        _messaging = FirebaseMessaging.GetMessaging(app);
    }

    public async Task<FcmSendResult> SendAsync(IReadOnlyList<FcmMessage> messages, CancellationToken ct)
    {
        if (messages.Count == 0)
        {
            return new FcmSendResult([], 0, 0);
        }

        // A hung FCM call must not hold a printer webhook open. Linked so a genuine caller
        // cancellation still propagates, and so the two causes can be told apart in logs.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.SendTimeoutSeconds));

        var payloads = messages.Select(m => new Message
        {
            Token = m.Token,
            Notification = new Notification { Title = m.Title, Body = m.Body },
            Data = m.Data,
            Android = new AndroidConfig
            {
                Notification = new AndroidNotification
                {
                    // Without an explicit channel the notification lands in the default
                    // channel and the user's per-category mute silently stops working.
                    ChannelId = _options.ChannelId,
                    // Tagging on the notification id means a redelivered duplicate replaces
                    // the existing card instead of stacking a second one.
                    Tag = m.Data.TryGetValue("notificationId", out var id) ? id : null
                },
                // A phone offline for a week should not announce on reconnect that a print
                // "just failed".
                TimeToLive = TimeSpan.FromHours(_options.TimeToLiveHours),
                Priority = Priority.High
            }
        }).ToList();

        BatchResponse response;
        try
        {
            response = await _messaging.SendEachAsync(payloads, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "FCM send timed out after {Seconds}s; {Count} message(s) may or may not have been accepted.",
                _options.SendTimeoutSeconds, messages.Count);
            return new FcmSendResult([], 0, messages.Count);
        }

        var unregistered = new List<string>();
        for (var i = 0; i < response.Responses.Count; i++)
        {
            var r = response.Responses[i];
            if (r.IsSuccess)
            {
                continue;
            }

            var code = r.Exception?.MessagingErrorCode;
            if (code == MessagingErrorCode.Unregistered)
            {
                unregistered.Add(messages[i].Token);
            }
            else
            {
                // Includes InvalidArgument, which can mean a bad PAYLOAD rather than a bad
                // token. Pruning on it would let one malformed message wipe every device
                // registration in the batch.
                _logger.LogWarning("FCM send failed with {ErrorCode}; keeping the token.", code);
            }
        }

        return new FcmSendResult(unregistered, response.SuccessCount, response.FailureCount);
    }
}
