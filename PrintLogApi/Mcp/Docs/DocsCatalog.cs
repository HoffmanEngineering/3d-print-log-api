using System.Text;
using Microsoft.Extensions.Options;

namespace PrintLogApi.Mcp.Docs;

/// <summary>The docs as last loaded. <see cref="Available"/> is false when the index could not be read.</summary>
public sealed class DocsCorpus
{
    public static readonly DocsCorpus Unavailable = new(false, []);

    private readonly Dictionary<string, DocsDocument> _bySlug;

    public DocsCorpus(bool available, IReadOnlyList<DocsDocument> documents)
    {
        Available = available;
        Documents = documents;
        _bySlug = documents.ToDictionary(d => d.Page.Slug, StringComparer.Ordinal);
    }

    public bool Available { get; }

    public IReadOnlyList<DocsDocument> Documents { get; }

    public DocsDocument? Find(string slug) => _bySlug.GetValueOrDefault(slug);
}

public interface IDocsCatalog
{
    /// <summary>
    /// The docs corpus. Never throws for a fetch failure: an unreachable or missing index yields
    /// <see cref="DocsCorpus.Unavailable"/>, and a page that fails to load is left out.
    /// </summary>
    Task<DocsCorpus> GetCorpusAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Fetches the Markdown twins of the docs pages from the configured site origin and keeps them in
/// memory.
///
/// <para>The whole corpus is held in a field rather than in <c>IMemoryCache</c>. It is one value
/// of a few hundred kilobytes that every docs call needs, it is the same for every caller, and it
/// is not compute-on-miss over the database: putting it in the shared, size-limited cache would
/// only let a burst of per-user entries evict it and turn the next anonymous call into ~25 fetches.
/// A single gate gives the same stampede protection <c>HybridCache</c> would.</para>
///
/// <para>A load runs under its own timeout rather than the caller's token, so one client
/// disconnecting cannot fail the load every other waiter is sharing.</para>
/// </summary>
public sealed class DocsCatalog(
    IHttpClientFactory httpClientFactory,
    IOptions<DocsOptions> options,
    TimeProvider timeProvider,
    ILogger<DocsCatalog> logger) : IDocsCatalog, IDisposable
{
    public const string HttpClientName = "Docs";

    private const int PageFetchConcurrency = 4;
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Snapshot? _current;

    private sealed record Snapshot(DocsCorpus Corpus, DateTimeOffset ExpiresAt);

    public async Task<DocsCorpus> GetCorpusAsync(CancellationToken cancellationToken)
    {
        var current = _current;
        if (current is not null && timeProvider.GetUtcNow() < current.ExpiresAt)
        {
            return current.Corpus;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            current = _current;
            if (current is not null && timeProvider.GetUtcNow() < current.ExpiresAt)
            {
                return current.Corpus;
            }

            var settings = options.Value;
            var (corpus, complete) = await LoadAsync(settings);
            var ttl = TimeSpan.FromMinutes(complete ? settings.CacheMinutes : settings.FailureCacheMinutes);
            _current = new Snapshot(corpus, timeProvider.GetUtcNow() + ttl);
            return corpus;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>Drops the loaded corpus so the next call fetches again. For tests.</summary>
    internal void Reset() => _current = null;

    private async Task<(DocsCorpus Corpus, bool Complete)> LoadAsync(DocsOptions settings)
    {
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var origin)
            || (origin.Scheme != Uri.UriSchemeHttps && origin.Scheme != Uri.UriSchemeHttp))
        {
            logger.LogWarning("Docs:BaseUrl is not an absolute http(s) URL; the MCP docs surface is empty.");
            return (DocsCorpus.Unavailable, false);
        }

        using var timeout = new CancellationTokenSource(LoadTimeout);
        var client = httpClientFactory.CreateClient(HttpClientName);

        try
        {
            var indexUrl = new Uri($"{origin.GetLeftPart(UriPartial.Authority)}/docs/llms.txt");
            var index = await ReadBoundedAsync(client, indexUrl, settings.MaxIndexBytes, timeout.Token);
            if (index is null)
            {
                return (DocsCorpus.Unavailable, false);
            }

            var pages = DocsIndex.Parse(index, origin, settings.MaxPages);
            if (pages.Count == 0)
            {
                logger.LogWarning("The docs index at {IndexUrl} lists no pages.", indexUrl);
                return (DocsCorpus.Unavailable, false);
            }

            var bodies = new string?[pages.Count];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, pages.Count),
                new ParallelOptions { MaxDegreeOfParallelism = PageFetchConcurrency, CancellationToken = timeout.Token },
                async (i, ct) =>
                {
                    // Rebuilt from the configured origin and the validated slug, never the
                    // index's own link text.
                    var url = new Uri(DocsIndex.MarkdownUrl(origin, pages[i].Slug));
                    try
                    {
                        bodies[i] = await ReadBoundedAsync(client, url, settings.MaxPageBytes, ct);
                    }
                    catch (Exception ex) when (
                        ex is HttpRequestException or IOException
                        || (ex is OperationCanceledException && !ct.IsCancellationRequested))
                    {
                        // One page failing (or hitting the per-request timeout) leaves it out;
                        // only the overall timeout abandons the load.
                        logger.LogWarning(ex, "Fetching {DocsUrl} failed.", url);
                    }
                });

            var documents = new List<DocsDocument>(pages.Count);
            for (var i = 0; i < pages.Count; i++)
            {
                if (bodies[i] is { } body)
                {
                    documents.Add(new DocsDocument(pages[i], body));
                }
            }

            if (documents.Count == 0)
            {
                return (DocsCorpus.Unavailable, false);
            }

            return (new DocsCorpus(true, documents), documents.Count == pages.Count);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            logger.LogWarning(ex, "Loading the docs for the MCP docs surface failed.");
            return (DocsCorpus.Unavailable, false);
        }
    }

    /// <summary>
    /// The body as text, or null for a non-success status, an HTML response (the site's 404 page
    /// or app shell, never a twin) or a body over <paramref name="maxBytes"/>.
    /// </summary>
    private async Task<string?> ReadBoundedAsync(HttpClient client, Uri url, int maxBytes, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Fetching {DocsUrl} returned {StatusCode}.", url, (int)response.StatusCode);
            return null;
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Fetching {DocsUrl} returned HTML, not Markdown.", url);
            return null;
        }

        if (response.Content.Headers.ContentLength > maxBytes)
        {
            logger.LogWarning("{DocsUrl} is larger than the {MaxBytes}-byte limit.", url, maxBytes);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                logger.LogWarning("{DocsUrl} is larger than the {MaxBytes}-byte limit.", url, maxBytes);
                return null;
            }
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
