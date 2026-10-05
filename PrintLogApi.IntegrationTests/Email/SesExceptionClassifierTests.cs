using System.Net;
using System.Net.Sockets;
using Amazon.Runtime;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using PrintLogApi.Email.Transport;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email;

public class SesExceptionClassifierTests
{
    public static TheoryData<Exception, EmailSendFailureKind> Cases => new()
    {
        { new TooManyRequestsException("slow down"), EmailSendFailureKind.Throttled },
        { new LimitExceededException("quota"), EmailSendFailureKind.Throttled },
        { new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused)), EmailSendFailureKind.NotConnected },
        { new HttpRequestException("dns", new SocketException((int)SocketError.HostNotFound)), EmailSendFailureKind.NotConnected },
        { new HttpRequestException(HttpRequestError.NameResolutionError, "dns"), EmailSendFailureKind.NotConnected },
        { new MessageRejectedException("bad"), EmailSendFailureKind.Rejected },
        { new BadRequestException("bad"), EmailSendFailureKind.Rejected },
        { new MailFromDomainNotVerifiedException("dns"), EmailSendFailureKind.Rejected },
        { new AccountSuspendedException("stop"), EmailSendFailureKind.AccountPaused },
        { new SendingPausedException("stop"), EmailSendFailureKind.AccountPaused },
        { new TaskCanceledException("timeout"), EmailSendFailureKind.Ambiguous },
        { new IOException("reset"), EmailSendFailureKind.Ambiguous },
        { new HttpRequestException("reset", new SocketException((int)SocketError.ConnectionReset)), EmailSendFailureKind.Ambiguous },
        { ServiceError(HttpStatusCode.ServiceUnavailable), EmailSendFailureKind.Ambiguous },
        { ServiceError(HttpStatusCode.InternalServerError), EmailSendFailureKind.Ambiguous },
        { new InvalidOperationException("unknown"), EmailSendFailureKind.Ambiguous },
    };

    private static AmazonSimpleEmailServiceV2Exception ServiceError(HttpStatusCode status)
        => new("server error", null, ErrorType.Receiver, "InternalFailure", "req-1", status);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Classify(Exception ex, EmailSendFailureKind expected)
        => Assert.Equal(expected, SesExceptionClassifier.Classify(ex));

    // SES answered with a 4xx we do not list: it received the request and declined it, so the
    // message was not sent and a retry would fail the same way.
    [Fact]
    public void UnlistedClientError_IsRejected()
        => Assert.Equal(EmailSendFailureKind.Rejected, SesExceptionClassifier.Classify(ServiceError(HttpStatusCode.Forbidden)));
}
