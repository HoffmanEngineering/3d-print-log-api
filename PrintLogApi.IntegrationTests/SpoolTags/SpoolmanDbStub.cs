using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace PrintLogApi.IntegrationTests.SpoolTags;

/// <summary>
/// Stands in for donkie.github.io in every test host, so no test reaches the network. Unconfigured,
/// it answers 404. <see cref="Serve"/> behaves like the real file host: it returns 304 when the
/// request carries the current ETag in <c>If-None-Match</c>.
/// </summary>
public sealed class SpoolmanDbStub
{
    private Func<HttpRequestMessage, HttpResponseMessage> _responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    /// <summary>The <c>If-None-Match</c> value of each request, in order (null when absent).</summary>
    public ConcurrentQueue<string?> IfNoneMatch { get; } = new();

    public int RequestCount => IfNoneMatch.Count;

    public void Reset()
    {
        _responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        IfNoneMatch.Clear();
    }

    public void Serve(string body, string? etag = "\"v1\"")
    {
        _responder = request =>
        {
            if (etag != null && request.Headers.IfNoneMatch.Any(t => t.ToString() == etag))
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (etag != null)
            {
                response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(etag);
            }
            return response;
        };
    }

    /// <summary>Fails the request the way HttpClient does when its timeout elapses.</summary>
    public void ServeTimeout() => _responder = _ => throw new TaskCanceledException("The request timed out.");

    public void ServeStatus(HttpStatusCode status) => _responder = _ => new HttpResponseMessage(status);

    internal HttpResponseMessage Respond(HttpRequestMessage request)
    {
        IfNoneMatch.Enqueue(request.Headers.IfNoneMatch.Count == 0 ? null : string.Join(", ", request.Headers.IfNoneMatch));
        var response = _responder(request);
        response.RequestMessage = request;
        return response;
    }

    /// <summary>A fresh handler per pipeline; the shared state lives in the stub.</summary>
    public HttpMessageHandler CreateHandler() => new Handler(this);

    private sealed class Handler(SpoolmanDbStub stub) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(stub.Respond(request));
    }
}
