using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrintLogApi.Webhooks;
using Xunit;

namespace PrintLogApi.IntegrationTests.Webhooks;

/// <summary>One delivery attempt (#235): the status decides, whatever the body does afterward.</summary>
public class WebhookSenderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class HandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond());
    }

    /// <summary>A body that sends nothing until the reader gives up.</summary>
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private static WebhookSender Sender(Func<HttpResponseMessage> respond) => new(
        new HandlerFactory(new Handler(respond)),
        Options.Create(new WebhookOptions { Timeout = TimeSpan.FromMilliseconds(300) }),
        TimeProvider.System,
        NullLogger<WebhookSender>.Instance);

    private static Task<WebhookAttemptResult> Send(WebhookSender sender) => sender.SendAsync(
        new Uri("https://hooks.example.test/api/webhook/x"), "whsec_x",
        new WebhookMessage("print.finished", "evt_x", Encoding.UTF8.GetBytes("{}")), 1, Ct);

    [Fact]
    public async Task ASuccessWithASlowBody_IsASuccess_NotATimeoutToRetry()
    {
        var sender = Sender(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });

        var result = await Send(sender);

        Assert.True(result.Succeeded);
        Assert.Equal(200, result.StatusCode);
        Assert.False(result.Retryable);
    }

    [Fact]
    public async Task AnErrorWithASlowBody_KeepsItsStatus()
    {
        var sender = Sender(() => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StreamContent(new StalledStream()) });

        var result = await Send(sender);

        Assert.Equal(WebhookDeliveryResults.HttpError, result.Result);
        Assert.Equal(404, result.StatusCode);
        Assert.False(result.Retryable);
    }
}
