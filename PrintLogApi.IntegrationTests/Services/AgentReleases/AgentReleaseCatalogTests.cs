using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrintLogApi.IntegrationTests.Analytics;
using PrintLogApi.IntegrationTests.Mcp.Docs;
using PrintLogApi.Services.AgentReleases;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services.AgentReleases;

/// <summary>
/// <see cref="AgentReleaseCatalog"/> on its own, against a stub GitHub: which tag it takes as the
/// latest release, and how long it keeps each outcome (printlog-bridge#39).
/// </summary>
public class AgentReleaseCatalogTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private const string TagsPath = "/repos/HoffmanEngineering/printlog-bridge/tags?per_page=100";

    private readonly DocsSiteStub _github = new();
    private readonly SettableTimeProvider _clock = new(Start);
    private readonly AgentReleaseOptions _options = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class StubClientFactory(DocsSiteStub site) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(site.CreateHandler());
    }

    private AgentReleaseCatalog Catalog() => new(
        new StubClientFactory(_github), Options.Create(_options), _clock, NullLogger<AgentReleaseCatalog>.Instance);

    private void SetTags(params string[] names) => _github.Set(
        TagsPath,
        "[" + string.Join(",", names.Select(n => $$$"""{"name":"{{{n}}}","commit":{"sha":"abc"}}""")) + "]",
        "application/json");

    [Fact]
    public async Task TheLatestIsTheHighestReleaseTag_ComparedAsNumbers()
    {
        SetTags("v0.9.0", "v0.10.0", "v0.2.1");

        Assert.Equal("0.10.0", await Catalog().GetLatestVersionAsync("moonraker", Ct));
    }

    [Fact]
    public async Task PrereleasesAndOtherTags_AreIgnored()
    {
        SetTags("v0.2.0", "v0.3.0-rc.1", "edge", "release-9", "v1.0");

        Assert.Equal("0.2.0", await Catalog().GetLatestVersionAsync("moonraker", Ct));
    }

    [Fact]
    public async Task TheKindIsMatchedIgnoringCase()
    {
        SetTags("v0.2.0");

        Assert.Equal("0.2.0", await Catalog().GetLatestVersionAsync("Moonraker", Ct));
    }

    [Fact]
    public async Task AKindWithNoKnownAgent_IsNull_AndFetchesNothing()
    {
        SetTags("v0.2.0");

        Assert.Null(await Catalog().GetLatestVersionAsync("octoprint", Ct));
        Assert.Empty(_github.Requests);
    }

    [Fact]
    public async Task NoReleaseTagsYet_IsNull()
    {
        SetTags("edge");

        Assert.Null(await Catalog().GetLatestVersionAsync("moonraker", Ct));
    }

    [Fact]
    public async Task AFailedFetch_IsNull_AndDoesNotThrow()
    {
        _github.Set(TagsPath, """{"message":"API rate limit exceeded"}""", "application/json", HttpStatusCode.Forbidden);

        Assert.Null(await Catalog().GetLatestVersionAsync("moonraker", Ct));
    }

    [Fact]
    public async Task AnswersThatAreNotATagList_AreNull()
    {
        _github.Set(TagsPath, "<!doctype html><title>hi</title>", "text/html");

        Assert.Null(await Catalog().GetLatestVersionAsync("moonraker", Ct));
    }

    [Fact]
    public async Task TheAnswerIsKept_UntilItExpires()
    {
        SetTags("v0.2.0");
        var catalog = Catalog();
        await catalog.GetLatestVersionAsync("moonraker", Ct);

        SetTags("v0.3.0");
        _clock.SetUtcNow(Start.AddMinutes(_options.CacheMinutes - 1));
        Assert.Equal("0.2.0", await catalog.GetLatestVersionAsync("moonraker", Ct));
        Assert.Equal(1, _github.CountRequests(TagsPath));

        _clock.SetUtcNow(Start.AddMinutes(_options.CacheMinutes));
        Assert.Equal("0.3.0", await catalog.GetLatestVersionAsync("moonraker", Ct));
        Assert.Equal(2, _github.CountRequests(TagsPath));
    }

    [Fact]
    public async Task AFailedReload_KeepsTheVersionFoundEarlier_AndRetriesSooner()
    {
        SetTags("v0.2.0");
        var catalog = Catalog();
        await catalog.GetLatestVersionAsync("moonraker", Ct);

        _github.Remove(TagsPath);
        _clock.SetUtcNow(Start.AddMinutes(_options.CacheMinutes));
        Assert.Equal("0.2.0", await catalog.GetLatestVersionAsync("moonraker", Ct));

        SetTags("v0.3.0");
        _clock.SetUtcNow(Start.AddMinutes(_options.CacheMinutes + _options.FailureCacheMinutes));
        Assert.Equal("0.3.0", await catalog.GetLatestVersionAsync("moonraker", Ct));
    }

    [Fact]
    public async Task AFailure_IsKeptForTheFailureTime_SoEveryHeartbeatDoesNotAskGitHub()
    {
        var catalog = Catalog();
        await catalog.GetLatestVersionAsync("moonraker", Ct);
        await catalog.GetLatestVersionAsync("moonraker", Ct);

        Assert.Equal(1, _github.CountRequests(TagsPath));
    }

    [Fact]
    public async Task ARepositoryCanBeConfigured()
    {
        _options.Repositories["moonraker"] = "someone/fork";
        _github.Set("/repos/someone/fork/tags?per_page=100", """[{"name":"v9.9.9"}]""", "application/json");

        Assert.Equal("9.9.9", await Catalog().GetLatestVersionAsync("moonraker", Ct));
    }
}
