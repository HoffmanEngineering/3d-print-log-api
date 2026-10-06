using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using PrintLogApi.IntegrationTests.Mcp.Docs;
using PrintLogApi.Mcp.Apps;
using PrintLogApi.Mcp.Docs;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp.Apps;

/// <summary>
/// MCP Apps views (api#130): the inventory tool advertises a ui:// view that /mcp can read, the
/// view is self-contained, the anonymous docs endpoint never sees it, and the tool's own result
/// is what it was before the view existed.
/// </summary>
public class McpAppResourcesTests : IClassFixture<McpDocsWebApplicationFactory>
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly McpDocsWebApplicationFactory _factory;

    public McpAppResourcesTests(McpDocsWebApplicationFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> ReadInventoryHtml(ModelContextProtocol.Client.McpClient client)
    {
        var result = await client.ReadResourceAsync(McpAppResources.InventoryUri, cancellationToken: Ct);
        return Assert.IsType<TextResourceContents>(Assert.Single(result.Contents)).Text;
    }

    private static void AssertTightUiMeta(JsonObject? meta)
    {
        Assert.NotNull(meta);
        var ui = Assert.IsType<JsonObject>(meta["ui"]);
        var csp = Assert.IsType<JsonObject>(ui["csp"]);
        foreach (var directive in new[] { "connectDomains", "resourceDomains", "frameDomains", "baseUriDomains" })
        {
            Assert.Empty(Assert.IsType<JsonArray>(csp[directive]));
        }
        Assert.Equal(4, csp.Count);
        Assert.True(ui["prefersBorder"]!.GetValue<bool>());
        Assert.Null(ui["permissions"]);
        Assert.Null(ui["domain"]);
    }

    // ---- The tool links to a view that exists ----------------------------------------------

    [Fact]
    public async Task InventoryTool_AdvertisesTheInventoryView()
    {
        await using var client = await _factory.ConnectAsync();
        var tool = (await client.ListToolsAsync(cancellationToken: Ct))
            .Single(t => t.Name == "get_material_inventory").ProtocolTool;

        Assert.NotNull(tool.Meta);
        Assert.Equal(McpAppResources.InventoryUri, tool.Meta["ui"]?["resourceUri"]?.GetValue<string>());
        Assert.Equal(McpAppResources.InventoryUri, tool.Meta["openai/outputTemplate"]?.GetValue<string>());
        // The deprecated flat key is not emitted (SEP-1865 removes it before GA).
        Assert.False(tool.Meta.ContainsKey("ui/resourceUri"));
    }

    /// <summary>Every view any tool points at can be read, so no tool links to a missing view.</summary>
    [Fact]
    public async Task EveryAdvertisedView_IsReadable()
    {
        await using var client = await _factory.ConnectAsync(
            IntegrationTestSeeder.TestUserOAuthId, new[] { "read:printdata", "write:printdata" });
        var uris = (await client.ListToolsAsync(cancellationToken: Ct))
            .Select(t => t.ProtocolTool.Meta?["ui"]?["resourceUri"]?.GetValue<string>())
            .OfType<string>()
            .ToList();

        Assert.NotEmpty(uris);
        foreach (var uri in uris)
        {
            Assert.Contains(uri, McpAppResources.Uris);
            var result = await client.ReadResourceAsync(uri, cancellationToken: Ct);
            Assert.Equal(McpAppResources.MimeType, Assert.Single(result.Contents).MimeType);
        }
    }

    [Fact]
    public async Task ReadView_ReturnsHtmlWithTheAppMimeTypeAndATightCsp()
    {
        await using var client = await _factory.ConnectAsync();
        var result = await client.ReadResourceAsync(McpAppResources.InventoryUri, cancellationToken: Ct);

        var content = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents));
        Assert.Equal(McpAppResources.InventoryUri, content.Uri);
        Assert.Equal("text/html;profile=mcp-app", content.MimeType);
        Assert.StartsWith("<!doctype html>", content.Text, StringComparison.OrdinalIgnoreCase);
        AssertTightUiMeta(content.Meta);
    }

    [Fact]
    public async Task ListResources_OnMcp_HasTheViewBesideTheDocs()
    {
        await using var client = await _factory.ConnectAsync();
        var resources = await client.ListResourcesAsync(cancellationToken: Ct);

        var view = resources.Single(r => r.Uri == McpAppResources.InventoryUri).ProtocolResource;
        Assert.Equal(McpAppResources.MimeType, view.MimeType);
        AssertTightUiMeta(view.Meta);
        Assert.Contains(resources, r => r.Uri == "docs://klipper");
    }

    [Theory]
    [InlineData("ui://3d-print-log/nope")]
    [InlineData("ui://3d-print-log/material-inventory/")]
    [InlineData("ui://3D-Print-Log/material-inventory")]
    public async Task ReadUnknownView_IsResourceNotFound(string uri)
    {
        await using var client = await _factory.ConnectAsync();
        var ex = await Assert.ThrowsAnyAsync<McpProtocolException>(
            () => client.ReadResourceAsync(uri, cancellationToken: Ct).AsTask());

        Assert.Equal(McpErrorCode.ResourceNotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task DocsResources_AreStillReadableOnMcp()
    {
        await using var client = await _factory.ConnectAsync();
        var result = await client.ReadResourceAsync("docs://klipper", cancellationToken: Ct);

        Assert.Equal("text/markdown", Assert.Single(result.Contents).MimeType);
    }

    // ---- The anonymous docs endpoint never sees a view --------------------------------------

    [Fact]
    public async Task DocsEndpoint_DoesNotListTheViews()
    {
        await using var client = await _factory.ConnectToAsync(McpDocsEndpoint.Path);
        var resources = await client.ListResourcesAsync(cancellationToken: Ct);

        Assert.NotEmpty(resources);
        Assert.DoesNotContain(resources, r => r.Uri.StartsWith(McpAppResources.Scheme, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DocsEndpoint_CannotReadAView()
    {
        await using var client = await _factory.ConnectToAsync(McpDocsEndpoint.Path);
        var ex = await Assert.ThrowsAnyAsync<McpProtocolException>(
            () => client.ReadResourceAsync(McpAppResources.InventoryUri, cancellationToken: Ct).AsTask());

        Assert.Equal(McpErrorCode.ResourceNotFound, ex.ErrorCode);
    }

    // ---- The tool's own result is unchanged --------------------------------------------------

    private sealed record Item(Guid Id, string Name, double RemainingGrams, bool IsActive);
    private sealed record PageResult(List<Item> Items, int Page, int PageSize, int TotalCount, int TotalPages);

    /// <summary>
    /// Most clients do not render apps, so the result stays one JSON text block with no
    /// structuredContent or output schema. The view parses that same block.
    /// </summary>
    [Fact]
    public async Task InventoryTool_ResultIsStillOneJsonTextBlock()
    {
        await using var client = await _factory.ConnectAsync();
        var tool = (await client.ListToolsAsync(cancellationToken: Ct))
            .Single(t => t.Name == "get_material_inventory").ProtocolTool;
        Assert.Null(tool.OutputSchema);

        var result = await client.CallToolAsync(
            "get_material_inventory", new Dictionary<string, object?> { ["pageSize"] = 100 }, cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        Assert.Null(result.StructuredContent);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        var page = JsonSerializer.Deserialize<PageResult>(text, Json)!;
        Assert.Contains(page.Items, i => i.Id == IntegrationTestSeeder.TestFilamentId1 && i.RemainingGrams == 975.0);
        // The view reads these camelCase names; renaming one breaks it silently.
        using var doc = JsonDocument.Parse(text);
        var first = doc.RootElement.GetProperty("items")[0];
        foreach (var name in new[] { "name", "material", "remainingGrams", "isActive" })
        {
            Assert.True(first.TryGetProperty(name, out _), $"item is missing '{name}'");
        }
        Assert.True(doc.RootElement.TryGetProperty("totalCount", out _));
    }

    // ---- The view is self-contained ---------------------------------------------------------

    [Fact]
    public async Task View_ReferencesNothingExternal()
    {
        await using var client = await _factory.ConnectAsync();
        var html = await ReadInventoryHtml(client);

        Assert.DoesNotMatch(new Regex(@"https?:", RegexOptions.IgnoreCase), html);
        Assert.DoesNotMatch(new Regex(@"\b(src|href|action)\s*=", RegexOptions.IgnoreCase), html);
        Assert.DoesNotMatch(new Regex(@"<(link|iframe|object|embed|img|base)\b", RegexOptions.IgnoreCase), html);
        Assert.DoesNotMatch(new Regex(@"@import|url\(", RegexOptions.IgnoreCase), html);
        Assert.DoesNotMatch(new Regex(@"\b(fetch|XMLHttpRequest|WebSocket|EventSource|importScripts)\b"), html);
    }

    /// <summary>
    /// Values from the tool result are user-entered text (names, colors, storage locations), so
    /// the view writes them with textContent only.
    /// </summary>
    [Fact]
    public async Task View_NeverWritesMarkup()
    {
        await using var client = await _factory.ConnectAsync();
        var html = await ReadInventoryHtml(client);

        Assert.DoesNotMatch(new Regex(@"innerHTML|outerHTML|insertAdjacentHTML|document\.write|\beval\(|new Function"), html);
    }

    /// <summary>The view is presentation only: it never asks the host to call a tool.</summary>
    [Fact]
    public async Task View_NeverCallsATool()
    {
        await using var client = await _factory.ConnectAsync();
        var html = await ReadInventoryHtml(client);

        Assert.DoesNotContain("tools/call", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ui/message", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ui/update-model-context", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task View_DeclaresTheSpecVersionItWasBuiltAgainst()
    {
        await using var client = await _factory.ConnectAsync();
        var html = await ReadInventoryHtml(client);

        Assert.Contains($"PROTOCOL_VERSION = \"{McpAppResources.SpecVersion}\"", html, StringComparison.Ordinal);
    }
}
