using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace PrintLogApi.IntegrationTests.Mcp.Docs;

/// <summary>
/// Stands in for www.3dprintlog.com in every test host, so no test ever reaches the network.
/// Unconfigured paths answer like the real site's 404: status 404 with an HTML body.
/// </summary>
public sealed class DocsSiteStub
{
    public sealed record StubResponse(HttpStatusCode Status, string ContentType, string Body);

    private readonly ConcurrentDictionary<string, StubResponse> _responses = new(StringComparer.Ordinal);

    /// <summary>Every absolute URL requested, in order.</summary>
    public ConcurrentQueue<Uri> Requests { get; } = new();

    public void Set(string pathAndQuery, string body, string contentType = "text/markdown", HttpStatusCode status = HttpStatusCode.OK) =>
        _responses[pathAndQuery] = new StubResponse(status, contentType, body);

    public void Remove(string pathAndQuery) => _responses.TryRemove(pathAndQuery, out _);

    public void Clear()
    {
        _responses.Clear();
        Requests.Clear();
    }

    public int CountRequests(string pathAndQuery) => Requests.Count(u => u.PathAndQuery == pathAndQuery);

    internal HttpResponseMessage Respond(HttpRequestMessage request)
    {
        Requests.Enqueue(request.RequestUri!);
        var stub = _responses.GetValueOrDefault(request.RequestUri!.PathAndQuery)
            ?? new StubResponse(HttpStatusCode.NotFound, "text/html", "<!doctype html><title>404</title>");

        var content = new StringContent(stub.Body, Encoding.UTF8);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(stub.ContentType) { CharSet = "utf-8" };
        return new HttpResponseMessage(stub.Status) { Content = content, RequestMessage = request };
    }

    /// <summary>
    /// A fresh handler per pipeline. The client factory disposes primary handlers when it rotates
    /// them, so the shared state lives in the stub, not in the handler.
    /// </summary>
    public HttpMessageHandler CreateHandler() => new Handler(this);

    private sealed class Handler(DocsSiteStub stub) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(stub.Respond(request));
    }

    /// <summary>
    /// A small docs site in the shape <c>scripts/docs-twins.mjs</c> generates, plus index lines
    /// that must be ignored: another host, a path outside /docs, a query string, a bad slug.
    /// </summary>
    public void SeedSite(string origin = "https://www.3dprintlog.test")
    {
        Set("/docs/llms.txt", $"""
            # 3D Print Log Docs

            > User documentation for 3D Print Log.

            ## Start here

            - [Pro Subscription]({origin}/docs/pro-subscription.md): Everything included in a 3D Print Log Pro subscription.

            ## Integrations

            - [Log prints from Klipper]({origin}/docs/klipper.md): Automatically log prints from Klipper using Moonraker webhooks.
            - [Log prints from OctoPrint]({origin}/docs/octoprint-webhook.md): Connect OctoPrint to 3D Print Log with a webhook.
            - [Evil]({"https://evil.example"}/docs/evil.md): Another host.
            - [Traversal]({origin}/docs/../secrets.md): Outside docs.
            - [Query]({origin}/docs/klipper.md?x=1): A query string.
            - [Upper]({origin}/docs/Bad_Slug.md): Not a slug.
            - [Duplicate]({origin}/docs/klipper.md): Repeats klipper.

            ## Optional

            - [Home]({origin}/index.md): What 3D Print Log is.
            - [Site index]({origin}/llms.txt): The API and the MCP server.
            """, "text/plain");

        Set("/docs/klipper.md", $"""
            # Klipper & Moonraker

            > Automatically log prints from Klipper using Moonraker webhooks.

            HTML version: {origin}/docs/klipper. Last updated 2026-09-02.

            ## Klipper/Moonraker Notifier

            3D Print Log can receive print information from Klipper and Moonraker using the
            built-in notifier component. Add a [notifier] section to moonraker.conf.
            """);

        Set("/docs/pro-subscription.md", $"""
            # Pro Subscription

            > Everything included in a 3D Print Log Pro subscription.

            HTML version: {origin}/docs/pro-subscription. Last updated 2026-08-01.

            Pro includes unlimited photo storage, advanced analytics and higher limits.
            """);

        Set("/docs/octoprint-webhook.md", $"""
            # OctoPrint Webhook

            > Connect OctoPrint to 3D Print Log with a webhook.

            HTML version: {origin}/docs/octoprint-webhook. Last updated 2026-07-01.

            Install the OctoPrint-Webhooks plugin and paste your API key.
            """);
    }
}
