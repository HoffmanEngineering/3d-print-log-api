using System.Net.Sockets;
using Amazon.Runtime;
using Amazon.SimpleEmailV2.Model;

namespace PrintLogApi.Email.Transport;

/// <summary>
/// Maps an exception from the SES client to what it means for retrying. The bias is deliberate:
/// anything not provably "never reached SES" or "SES answered no" is <see cref="EmailSendFailureKind.Ambiguous"/>,
/// because SES SendEmail has no idempotency key and a retry after an unseen success is a duplicate.
/// </summary>
public static class SesExceptionClassifier
{
    public static EmailSendFailureKind Classify(Exception ex)
    {
        switch (ex)
        {
            case TooManyRequestsException or LimitExceededException:
                return EmailSendFailureKind.Throttled;
            case MessageRejectedException or BadRequestException or MailFromDomainNotVerifiedException:
                return EmailSendFailureKind.Rejected;
            case AccountSuspendedException or SendingPausedException:
                return EmailSendFailureKind.AccountPaused;
        }

        if (NeverConnected(ex))
        {
            return EmailSendFailureKind.NotConnected;
        }

        // SES answered: a 4xx means it read the request and declined it, so nothing was sent.
        // A 5xx says nothing about whether the message went out.
        if (ex is AmazonServiceException { StatusCode: var status } && (int)status is >= 400 and < 500)
        {
            return EmailSendFailureKind.Rejected;
        }

        return EmailSendFailureKind.Ambiguous;
    }

    /// <summary>
    /// True only for failures that happen before a byte of the request can have left: DNS and
    /// connection establishment. A reset or timeout on an open connection is not one of them.
    /// </summary>
    private static bool NeverConnected(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError })
            {
                return true;
            }

            if (e is SocketException
                {
                    SocketErrorCode: SocketError.ConnectionRefused or SocketError.HostNotFound
                or SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.TryAgain
                })
            {
                return true;
            }
        }

        return false;
    }
}
