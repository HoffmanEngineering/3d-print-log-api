using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrintLogApi.IntegrationTests.Analytics;
using PrintLogApi.Mcp.Docs;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp.Docs;

/// <summary>
/// <see cref="DocsCatalog"/> on its own, against <see cref="DocsSiteStub"/>: what it accepts from
/// the site, what it refuses, and how long it keeps each outcome.
/// </summary>
public class DocsCatalogTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly DocsSiteStub _site = new();
    private readonly SettableTimeProvider _clock = new(Start);
    private readonly DocsOptions _options = new() { BaseUrl = "https://www.3dprintlog.test" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class StubClientFactory(DocsSiteStub site) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(site.CreateHandler());
    }

    private DocsCatalog Catalog() => new(
        new StubClientFactory(_site), Options.Create(_options), _clock, NullLogger<DocsCatalog>.Instance);

    [Fact]
    public async Task ReadsTheIndexAndEveryPage()
    {
        _site.SeedSite();
        var corpus = await Catalog().GetCorpusAsync(Ct);

        Assert.True(corpus.Available);
        Assert.Equal(new[] { "pro-subscription", "klipper", "octoprint-webhook" }, corpus.Documents.Select(d => d.Page.Slug));
        Assert.StartsWith("# Klipper", corpus.Find("klipper")!.Markdown);
        Assert.Equal("Start here", corpus.Find("pro-subscription")!.Page.Section);
    }

    [Fact]
    public async Task AMissingIndex_IsUnavailable_AndDoesNotThrow()
    {
        var corpus = await Catalog().GetCorpusAsync(Ct);

        Assert.False(corpus.Available);
        Assert.Empty(corpus.Documents);
    }

    /// <summary>A site whose catch-all serves the app shell answers 200 with HTML for every path.</summary>
    [Fact]
    public async Task AnHtmlIndex_IsUnavailable()
    {
        _site.Set("/docs/llms.txt", "<!doctype html><html><body>- [x](https://www.3dprintlog.test/docs/x.md)</body></html>", "text/html");

        Assert.False((await Catalog().GetCorpusAsync(Ct)).Available);
    }

    [Fact]
    public async Task AnIndexWithNoPages_IsUnavailable()
    {
        _site.Set("/docs/llms.txt", "# 3D Print Log Docs\n\nNothing here.\n", "text/plain");

        Assert.False((await Catalog().GetCorpusAsync(Ct)).Available);
    }

    [Fact]
    public async Task AnOversizedIndex_IsRefused()
    {
        _site.SeedSite();
        _options.MaxIndexBytes = 64;

        Assert.False((await Catalog().GetCorpusAsync(Ct)).Available);
    }

    [Fact]
    public async Task AnOversizedPage_IsLeftOut()
    {
        _site.SeedSite();
        _site.Set("/docs/klipper.md", new string('x', 4096));
        _options.MaxPageBytes = 2048;

        var corpus = await Catalog().GetCorpusAsync(Ct);

        Assert.True(corpus.Available);
        Assert.Null(corpus.Find("klipper"));
        Assert.NotNull(corpus.Find("pro-subscription"));
    }

    [Fact]
    public async Task APageThatFails_IsLeftOut_AndTheRestServe()
    {
        _site.SeedSite();
        _site.Set("/docs/octoprint-webhook.md", "boom", "text/plain", HttpStatusCode.InternalServerError);

        var corpus = await Catalog().GetCorpusAsync(Ct);

        Assert.True(corpus.Available);
        Assert.Null(corpus.Find("octoprint-webhook"));
        Assert.NotNull(corpus.Find("klipper"));
    }

    [Fact]
    public async Task ARedirect_IsNotFollowed()
    {
        _site.Set("/docs/llms.txt", "", "text/plain", HttpStatusCode.Found);

        Assert.False((await Catalog().GetCorpusAsync(Ct)).Available);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://www.3dprintlog.test")]
    [InlineData("/docs")]
    public async Task ABadBaseUrl_IsUnavailable_WithoutFetching(string baseUrl)
    {
        _site.SeedSite();
        _options.BaseUrl = baseUrl;

        Assert.False((await Catalog().GetCorpusAsync(Ct)).Available);
        Assert.Empty(_site.Requests);
    }

    [Fact]
    public async Task ACompleteCorpus_IsServedFromMemoryUntilItExpires()
    {
        _site.SeedSite();
        var catalog = Catalog();

        await catalog.GetCorpusAsync(Ct);
        _clock.SetUtcNow(Start.AddMinutes(_options.CacheMinutes - 1));
        await catalog.GetCorpusAsync(Ct);
        Assert.Equal(1, _site.CountRequests("/docs/llms.txt"));

        _clock.SetUtcNow(Start.AddMinutes(_options.CacheMinutes + 1));
        await catalog.GetCorpusAsync(Ct);
        Assert.Equal(2, _site.CountRequests("/docs/llms.txt"));
    }

    /// <summary>
    /// A failure is remembered briefly, so an anonymous caller cannot turn every request into a
    /// fetch, and then retried, so a deploy that lands the twins is picked up within minutes.
    /// </summary>
    [Fact]
    public async Task AFailure_IsRememberedBriefly_ThenRetried()
    {
        var catalog = Catalog();

        Assert.False((await catalog.GetCorpusAsync(Ct)).Available);
        _site.SeedSite();
        Assert.False((await catalog.GetCorpusAsync(Ct)).Available);
        Assert.Equal(1, _site.CountRequests("/docs/llms.txt"));

        _clock.SetUtcNow(Start.AddMinutes(_options.FailureCacheMinutes + 1));
        Assert.True((await catalog.GetCorpusAsync(Ct)).Available);
    }

    [Fact]
    public async Task APartialCorpus_IsRetriedSooner()
    {
        _site.SeedSite();
        _site.Remove("/docs/klipper.md");
        var catalog = Catalog();

        Assert.Null((await catalog.GetCorpusAsync(Ct)).Find("klipper"));

        _site.SeedSite();
        _clock.SetUtcNow(Start.AddMinutes(_options.FailureCacheMinutes + 1));
        Assert.NotNull((await catalog.GetCorpusAsync(Ct)).Find("klipper"));
    }

    /// <summary>
    /// The docs change rarely, so a reload that fails after a good load keeps serving the copy
    /// already held, and retries after the short failure TTL instead of the full one.
    /// </summary>
    [Fact]
    public async Task AFailedReload_KeepsServingTheEarlierCopy_AndRetriesSoon()
    {
        _site.SeedSite();
        var catalog = Catalog();
        await catalog.GetCorpusAsync(Ct);

        _site.Set("/docs/llms.txt", "down", "text/plain", HttpStatusCode.ServiceUnavailable);
        _clock.SetUtcNow(Start.AddMinutes(_options.CacheMinutes + 1));
        var stale = await catalog.GetCorpusAsync(Ct);

        Assert.True(stale.Available);
        Assert.NotNull(stale.Find("klipper"));
        Assert.Equal(2, _site.CountRequests("/docs/llms.txt"));

        _site.SeedSite();
        _clock.SetUtcNow(Start.AddMinutes(_options.CacheMinutes + 1 + _options.FailureCacheMinutes + 1));
        await catalog.GetCorpusAsync(Ct);
        Assert.Equal(3, _site.CountRequests("/docs/llms.txt"));
    }

    [Fact]
    public async Task APartialReload_KeepsTheEarlierCompleteCopy()
    {
        _site.SeedSite();
        var catalog = Catalog();
        await catalog.GetCorpusAsync(Ct);

        _site.Remove("/docs/klipper.md");
        _clock.SetUtcNow(Start.AddMinutes(_options.CacheMinutes + 1));

        Assert.NotNull((await catalog.GetCorpusAsync(Ct)).Find("klipper"));
    }

    /// <summary>A partial copy is still better than none when the next reload fails outright.</summary>
    [Fact]
    public async Task AFailedReload_AfterAPartialLoad_KeepsThePartialCopy()
    {
        _site.SeedSite();
        _site.Remove("/docs/klipper.md");
        var catalog = Catalog();
        await catalog.GetCorpusAsync(Ct);

        _site.Remove("/docs/llms.txt");
        _clock.SetUtcNow(Start.AddMinutes(_options.FailureCacheMinutes + 1));
        var corpus = await catalog.GetCorpusAsync(Ct);

        Assert.True(corpus.Available);
        Assert.NotNull(corpus.Find("pro-subscription"));
    }

    [Fact]
    public async Task ConcurrentCallers_ShareOneLoad()
    {
        _site.SeedSite();
        var catalog = Catalog();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => catalog.GetCorpusAsync(Ct)));

        Assert.Equal(1, _site.CountRequests("/docs/llms.txt"));
    }
}
