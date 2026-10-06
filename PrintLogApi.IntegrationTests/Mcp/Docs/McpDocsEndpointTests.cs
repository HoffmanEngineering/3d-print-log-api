using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using PrintLogApi.Mcp.Docs;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp.Docs;

/// <summary>An MCP host whose docs site serves <see cref="DocsSiteStub.SeedSite"/>.</summary>
public class McpDocsWebApplicationFactory : McpDataWebApplicationFactory
{
    public McpDocsWebApplicationFactory() => DocsSite.SeedSite();
}

/// <summary>
/// The anonymous docs endpoint (#129): an MCP client with no token can list and read the docs
/// and call search_docs, and nothing about it opens the data tools to that client.
/// </summary>
public class McpDocsEndpointTests : IClassFixture<McpDocsWebApplicationFactory>
{
    private static readonly string[] DocsTools = { "get_doc", "list_docs", "search_docs" };
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly McpDocsWebApplicationFactory _factory;

    public McpDocsEndpointTests(McpDocsWebApplicationFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static T Parse<T>(CallToolResult result) =>
        JsonSerializer.Deserialize<T>(result.Content.OfType<TextContentBlock>().First().Text, Json)!;

    private static string Text(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().First().Text;

    private static HttpRequestMessage RpcPost(string path, string method, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"{method}\"}}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return request;
    }

    [Fact]
    public async Task Anonymous_ListsOnlyTheDocsTools()
    {
        await using var client = await _factory.ConnectToAsync(McpDocsEndpoint.Path);
        var tools = await client.ListToolsAsync(cancellationToken: Ct);

        Assert.Equal(DocsTools, tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Anonymous_GetsDocsInstructions()
    {
        await using var client = await _factory.ConnectToAsync(McpDocsEndpoint.Path);
        Assert.Equal(McpDocsEndpoint.Instructions, client.ServerInstructions);
    }

    [Fact]
    public async Task Anonymous_SearchDocsKlipper_FindsTheKlipperPage()
    {
        await using var client = await _factory.ConnectToAsync(McpDocsEndpoint.Path);
        var result = await client.CallToolAsync(
            "search_docs", new Dictionary<string, object?> { ["query"] = "klipper" }, cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        var search = Parse<DocsSearchResult>(result);
        var top = search.Results[0];
        Assert.Equal("klipper", top.Slug);
        Assert.Equal("docs://klipper", top.ResourceUri);
        Assert.Equal("https://www.3dprintlog.test/docs/klipper", top.Url);
        Assert.Equal("https://www.3dprintlog.test/docs/klipper.md", top.MarkdownUrl);
        Assert.Contains("Klipper", top.Excerpt);
        // The excerpt comes from the body, not the preamble that repeats the index entry.
        Assert.DoesNotContain("HTML version", top.Excerpt);
    }

    [Fact]
    public async Task SearchDocs_AQuestionRanksByTopicNotByStopWords()
    {
        await using var client = await _factory.ConnectToAsync(McpDocsEndpoint.Path);
        var result = await client.CallToolAsync(
            "search_docs", new Dictionary<string, object?> { ["query"] = "How do I connect Klipper?" }, cancellationToken: Ct);

        Assert.Equal("klipper", Parse<DocsSearchResult>(result).Results[0].Slug);
    }

    [Fact]
    public async Task SearchDocs_NoMatch_IsAnEmptyResultNotAnError()
    {
        await using var client = await _factory.ConnectToAsync(McpDocsEndpoint.Path);
        var result = await client.CallToolAsync(
            "search_docs", new Dictionary<string, object?> { ["query"] = "zzzqqq" }, cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        var search = Parse<DocsSearchResult>(result);
        Assert.Empty(search.Results);
        Assert.Equal(0, search.TotalMatches);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("the how to")]
    public async Task SearchDocs_QueryWithNothingToSearch_IsInvalidArguments(string query)
    {
        await using var client = await _factory.ConnectToAsync(McpDocsEndpoint.Path);
        var result = await client.CallToolAsync(
            "search_docs", new Dictionary<string, object?> { ["query"] = query }, cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.StartsWith("invalid_arguments:", Text(result));
    }

    [Fact]
    public async Task SearchDocs_LimitCapsTheResults()
    {
        await using var client = await _factory.ConnectToAsync(McpDocsEndpoint.Path);
        var result = await client.CallToolAsync(
            "search_docs", new Dictionary<string, object?> { ["query"] = "3D Print Log", ["limit"] = 1 }, cancellationToken: Ct);

        var search = Parse<DocsSearchResult>(result);
        Assert.Single(search.Results);
        Assert.True(search.TotalMatches > 1);
    }

    [Fact]
    public async Task ListDocs_ListsOnlyPagesOnTheConfiguredOrigin()
    {
        await using var client = await _factory.ConnectToAsync(McpDocsEndpoint.Path);
        var result = await client.CallToolAsync("list_docs", new Dictionary<string, object?>(), cancellationToken: Ct);

        var pages = Parse<List<DocsListItem>>(result);
        Assert.Equal(
            new[] { "pro-subscription", "klipper", "octoprint-webhook" },
            pages.Select(p => p.Slug));
        Assert.Equal("Integrations", pages[1].Section);
    }

    [Fact]
    public async Task GetDoc_ReturnsTheMarkdownTwin()
    {
        await using var client = await _factory.ConnectToAsync(McpDocsEndpoint.Path);
        var result = await client.CallToolAsync(
            "get_doc", new Dictionary<string, object?> { ["slug"] = "klipper" }, cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        Assert.StartsWith("# Klipper & Moonraker", Text(result));
    }

    [Fact]
    public async Task GetDoc_UnknownSlug_IsNotFound()
    {
        await using var client = await _factory.ConnectToAsync(McpDocsEndpoint.Path);
        var result = await client.CallToolAsync(
            "get_doc", new Dictionary<string, object?> { ["slug"] = "no-such-page" }, cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.StartsWith("not_found:", Text(result));
    }

    [Theory]
    [InlineData("../secrets")]
    [InlineData("https://evil.example/x")]
    [InlineData("Klipper")]
    [InlineData("klipper.md")]
    public async Task GetDoc_SomethingThatIsNotASlug_IsInvalidAndFetchesNothing(string slug)
    {
        await using var client = await _factory.ConnectToAsync(McpDocsEndpoint.Path);
        var result = await client.CallToolAsync(
            "get_doc", new Dictionary<string, object?> { ["slug"] = slug }, cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.StartsWith("invalid_arguments:", Text(result));
        Assert.DoesNotContain(_factory.DocsSite.Requests, u => u.Host != "www.3dprintlog.test");
    }

    [Fact]
    public async Task Anonymous_ListsTheDocsResources()
    {
        await using var client = await _factory.ConnectToAsync(McpDocsEndpoint.Path);
        var resources = await client.ListResourcesAsync(cancellationToken: Ct);

        Assert.Equal(
            new[] { "docs://klipper", "docs://octoprint-webhook", "docs://pro-subscription" },
            resources.Select(r => r.Uri).OrderBy(u => u, StringComparer.Ordinal));
        Assert.All(resources, r => Assert.Equal("text/markdown", r.MimeType));
        Assert.Contains(resources, r => r.Title == "Log prints from Klipper");
    }

    [Fact]
    public async Task Anonymous_ListsTheDocsResourceTemplate()
    {
        await using var client = await _factory.ConnectToAsync(McpDocsEndpoint.Path);
        var templates = await client.ListResourceTemplatesAsync(cancellationToken: Ct);

        Assert.Equal("docs://{slug}", Assert.Single(templates).UriTemplate);
    }

    [Fact]
    public async Task Anonymous_ReadsADocsResource()
    {
        await using var client = await _factory.ConnectToAsync(McpDocsEndpoint.Path);
        var result = await client.ReadResourceAsync("docs://klipper", cancellationToken: Ct);

        var content = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents));
        Assert.Equal("docs://klipper", content.Uri);
        Assert.Equal("text/markdown", content.MimeType);
        Assert.Contains("moonraker.conf", content.Text);
    }

    [Theory]
    [InlineData("docs://no-such-page")]
    [InlineData("docs://../secrets")]
    [InlineData("file:///etc/passwd")]
    public async Task ReadResource_UnknownUri_IsResourceNotFound(string uri)
    {
        await using var client = await _factory.ConnectToAsync(McpDocsEndpoint.Path);
        var ex = await Assert.ThrowsAnyAsync<McpProtocolException>(
            () => client.ReadResourceAsync(uri, cancellationToken: Ct).AsTask());

        Assert.Equal(McpErrorCode.ResourceNotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task OnlyTheConfiguredOriginIsEverFetched()
    {
        await using var client = await _factory.ConnectToAsync(McpDocsEndpoint.Path);
        await client.ListResourcesAsync(cancellationToken: Ct);

        var requested = _factory.DocsSite.Requests.ToList();
        Assert.NotEmpty(requested);
        Assert.All(requested, u =>
        {
            Assert.Equal("https", u.Scheme);
            Assert.Equal("www.3dprintlog.test", u.Host);
            Assert.True(u.AbsolutePath == "/docs/llms.txt" || u.AbsolutePath.StartsWith("/docs/", StringComparison.Ordinal),
                $"unexpected fetch {u}");
            Assert.Empty(u.Query);
        });
        Assert.DoesNotContain(requested, u => u.AbsolutePath is "/secrets.md" or "/docs/Bad_Slug.md" or "/docs/evil.md");
    }

    // ---- The data tools stay closed --------------------------------------------------------

    [Theory]
    [InlineData("ping")]
    [InlineData("search_prints")]
    [InlineData("get_print_summary")]
    [InlineData("create_print")]
    [InlineData("create_feedback")]
    public async Task Anonymous_CannotCallADataToolThroughTheDocsEndpoint(string tool)
    {
        await using var client = await _factory.ConnectToAsync(McpDocsEndpoint.Path);
        var isError = await McpDataWebApplicationFactory.IsToolError(
            client, tool, new Dictionary<string, object?> { ["message"] = "x", ["query"] = "x" });

        Assert.True(isError);
    }

    /// <summary>
    /// A valid MCP token does not turn the docs endpoint into the data endpoint: the collection is
    /// narrowed by path, not by who is calling.
    /// </summary>
    [Fact]
    public async Task ATokenOnTheDocsEndpoint_StillSeesOnlyTheDocsTools()
    {
        var token = TestJwt.Create(TestJwt.McpAudience, subject: IntegrationTestSeeder.TestUserOAuthId,
            scopes: new[] { "read:printdata", "write:printdata" });
        await using var client = await _factory.ConnectToAsync(McpDocsEndpoint.Path, token);

        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        Assert.Equal(DocsTools, tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.True(await McpDataWebApplicationFactory.IsToolError(
            client, "ping", new Dictionary<string, object?> { ["message"] = "x" }));
    }

    [Fact]
    public async Task Anonymous_CannotConnectToTheDataEndpoint()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => _factory.ConnectToAsync("/mcp"));
    }

    /// <summary>
    /// /mcp still answers an unauthenticated call exactly as it did before the docs endpoint
    /// existed: 401, an empty body (no ProblemDetails), and the RFC 9728 challenge an MCP client
    /// starts OAuth from.
    /// </summary>
    [Theory]
    [InlineData("initialize")]
    [InlineData("tools/list")]
    [InlineData("resources/list")]
    [InlineData("tools/call")]
    public async Task UnauthenticatedPostToMcp_IsStillTheSame401(string method)
    {
        var response = await _factory.CreateClient().SendAsync(RpcPost("/mcp", method), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            $"Bearer resource_metadata=\"http://localhost/.well-known/oauth-protected-resource/mcp\"",
            response.Headers.WwwAuthenticate.ToString());
        Assert.Empty(await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task UnauthenticatedPostToDocs_IsServedNotChallenged()
    {
        var response = await _factory.CreateClient().SendAsync(RpcPost(McpDocsEndpoint.Path, "tools/list"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Headers.WwwAuthenticate);
    }

    /// <summary>
    /// The narrowing runs on a per-request copy of the server options. If it ever mutated the
    /// shared registration, /mcp would lose its data tools after the first docs call.
    /// </summary>
    [Fact]
    public async Task ADocsCall_DoesNotNarrowTheDataEndpoint()
    {
        await using (var anonymous = await _factory.ConnectToAsync(McpDocsEndpoint.Path))
        {
            await anonymous.ListToolsAsync(cancellationToken: Ct);
        }

        await using var client = await _factory.ConnectAsync();
        var tools = (await client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ToList();
        Assert.Contains("search_prints", tools);
        Assert.Contains("ping", tools);
        Assert.Null(client.ServerInstructions);
    }

    /// <summary>Signed-in agents get the docs on /mcp too, so they need no second connection.</summary>
    [Fact]
    public async Task SignedIn_GetsTheDocsOnTheDataEndpoint()
    {
        await using var client = await _factory.ConnectAsync();

        var tools = (await client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ToList();
        Assert.Superset(DocsTools.ToHashSet(), tools.ToHashSet());

        var result = await client.CallToolAsync(
            "search_docs", new Dictionary<string, object?> { ["query"] = "octoprint" }, cancellationToken: Ct);
        Assert.Equal("octoprint-webhook", Parse<DocsSearchResult>(result).Results[0].Slug);

        var resources = await client.ListResourcesAsync(cancellationToken: Ct);
        Assert.Contains(resources, r => r.Uri == "docs://klipper");
    }

    [Fact]
    public async Task TheCorpusIsFetchedOnceAndThenServedFromMemory()
    {
        await using var client = await _factory.ConnectToAsync(McpDocsEndpoint.Path);
        await client.ListResourcesAsync(cancellationToken: Ct);
        var before = _factory.DocsSite.CountRequests("/docs/llms.txt");

        await client.CallToolAsync("search_docs", new Dictionary<string, object?> { ["query"] = "pro" }, cancellationToken: Ct);
        await client.ReadResourceAsync("docs://klipper", cancellationToken: Ct);

        Assert.Equal(before, _factory.DocsSite.CountRequests("/docs/llms.txt"));
    }
}
