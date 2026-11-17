using ModelContextProtocol.Protocol;
using PrintLogApi.Mcp.Docs;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp.Docs;

/// <summary>
/// The docs twins are not deployed yet, and the site can be down. The host must still start,
/// the docs surface must degrade to empty lists and a clear error, and /mcp must be unaffected.
/// This factory seeds nothing, so every docs fetch gets the site's 404.
/// </summary>
public class McpDocsUnavailableTests : IClassFixture<McpDataWebApplicationFactory>
{
    private readonly McpDataWebApplicationFactory _factory;

    public McpDocsUnavailableTests(McpDataWebApplicationFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListResources_IsEmpty()
    {
        await using var client = await _factory.ConnectToAsync(McpDocsEndpoint.Path);
        Assert.Empty(await client.ListResourcesAsync(cancellationToken: Ct));
    }

    [Theory]
    [InlineData("search_docs", "query", "klipper")]
    [InlineData("get_doc", "slug", "klipper")]
    [InlineData("list_docs", null, null)]
    public async Task DocsTools_ReturnUnavailable(string tool, string? argument, string? value)
    {
        await using var client = await _factory.ConnectToAsync(McpDocsEndpoint.Path);
        var arguments = new Dictionary<string, object?>();
        if (argument is not null)
        {
            arguments[argument] = value;
        }

        var result = await client.CallToolAsync(tool, arguments, cancellationToken: Ct);

        Assert.True(result.IsError);
        var text = result.Content.OfType<TextContentBlock>().First().Text;
        Assert.StartsWith("unavailable:", text);
        Assert.Contains("https://www.3dprintlog.com/docs", text);
    }

    [Fact]
    public async Task TheDataEndpointStillWorks()
    {
        await using var client = await _factory.ConnectAsync();
        var result = await client.CallToolAsync(
            "ping", new Dictionary<string, object?> { ["message"] = "hi" }, cancellationToken: Ct);

        Assert.Equal("pong: hi", result.Content.OfType<TextContentBlock>().First().Text);
    }
}
