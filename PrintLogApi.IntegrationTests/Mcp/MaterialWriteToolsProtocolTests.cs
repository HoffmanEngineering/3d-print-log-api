using System.Text.Json;
using ModelContextProtocol.Protocol;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp;

/// <summary>
/// End-to-end checks over the real /mcp endpoint for the material surface: tool naming,
/// annotations, scope visibility, and the guarantee that a WRITE-ONLY token can verify
/// everything it wrote from the tool's own response — without ever holding the read scope.
/// </summary>
public class MaterialWriteToolsProtocolTests : IClassFixture<McpDataWebApplicationFactory>
{
    private readonly McpDataWebApplicationFactory _factory;
    public MaterialWriteToolsProtocolTests(McpDataWebApplicationFactory factory) => _factory = factory;

    private static readonly string[] ReadWrite = { "read:printdata", "write:printdata" };
    private static readonly string[] WriteOnly = { "write:printdata" };
    private static readonly string[] ReadOnly = { "read:printdata" };

    private static string RawText(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().First().Text;

    private static Dictionary<string, object?> BasicArgs(string name) => new()
    {
        ["displayName"] = name,
        ["materialType"] = "PLA",
        ["materialCategoryNickname"] = "filament",
        ["densityGramPerCubicCm"] = 1.24,
        ["source"] = "Weight",
        ["initialAmount"] = 1000.0,
        ["diameterMm"] = 1.75,
    };

    [Fact]
    public async Task ToolList_ExposesRenamedTools_WithAnnotations()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(tools, t => t.Name == "add_material");

        var create = Assert.Single(tools, t => t.Name == "create_material");
        Assert.False(create.ProtocolTool.Annotations?.DestructiveHint);

        var update = Assert.Single(tools, t => t.Name == "update_material");
        // A capacity rebase changes a baseline; it never deletes the material or its history.
        Assert.False(update.ProtocolTool.Annotations?.DestructiveHint);

        var get = Assert.Single(tools, t => t.Name == "get_material");
        Assert.True(get.ProtocolTool.Annotations?.ReadOnlyHint);
    }

    [Fact]
    public async Task ReadOnlyToken_CannotSeeMaterialWriteTools()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadOnly);
        var names = (await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).Select(t => t.Name).ToHashSet();
        Assert.DoesNotContain("create_material", names);
        Assert.DoesNotContain("update_material", names);
        Assert.Contains("get_material", names);
    }

    [Fact]
    public async Task WriteOnlyToken_ReadsBackEveryFieldFromTheToolResult()
    {
        // The point of returning full detail: an agent holding ONLY write:printdata must be able
        // to confirm what it wrote without get_material, which it cannot call.
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, WriteOnly);

        var args = BasicArgs("protocol-material");
        args["brand"] = "Acme";
        args["colors"] = new[] { "FF8800", "112233" };
        args["storageLocation"] = "Shelf Z";
        args["recommendedTempC"] = 205.0;
        args["idempotencyKey"] = "mat-proto-1";

        var result = await client.CallToolAsync("create_material", args, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError != true);

        using var doc = JsonDocument.Parse(RawText(result));
        var material = doc.RootElement.GetProperty("material");
        Assert.Equal("Acme", material.GetProperty("brand").GetString());
        Assert.Equal("FF8800", material.GetProperty("colorHex").GetString());
        Assert.Equal("Shelf Z", material.GetProperty("storageLocation").GetString());
        Assert.Equal(205.0, material.GetProperty("recommendedTempC").GetDouble());
        Assert.Equal("Weight", material.GetProperty("sourceUnit").GetString());
        Assert.True(material.GetProperty("hasNominalCapacity").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("wasReplayed").GetBoolean());
    }

    [Fact]
    public async Task GetMaterial_OverTheWire_ReturnsDetail_AndForeignIdIsNotFound()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadOnly);

        var ok = await client.CallToolAsync("get_material", new Dictionary<string, object?>
        {
            ["materialId"] = McpTestData.ResinMaterialId,
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(ok.IsError != true);
        using (var doc = JsonDocument.Parse(RawText(ok)))
        {
            Assert.Equal("Elegoo Grey Standard Resin", doc.RootElement.GetProperty("displayName").GetString());
            Assert.Equal("Weight", doc.RootElement.GetProperty("sourceUnit").GetString());
        }

        var foreign = await client.CallToolAsync("get_material", new Dictionary<string, object?>
        {
            ["materialId"] = McpTestData.ForeignMaterialId,
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(foreign.IsError == true);
        Assert.StartsWith("not_found:", RawText(foreign));
    }

    [Fact]
    public async Task ReusedKeyWithDifferentArguments_SurfacesConflict()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

        Dictionary<string, object?> Args(string brand)
        {
            var a = BasicArgs("proto-conflict-material");
            a["brand"] = brand;
            a["idempotencyKey"] = "mat-proto-conflict";
            return a;
        }

        var first = await client.CallToolAsync("create_material", Args("Original"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(first.IsError != true);

        var replay = await client.CallToolAsync("create_material", Args("Original"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(replay.IsError != true);
        using (var replayDoc = JsonDocument.Parse(RawText(replay)))
        {
            Assert.True(replayDoc.RootElement.GetProperty("wasReplayed").GetBoolean());
        }

        var conflict = await client.CallToolAsync("create_material", Args("CHANGED"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(conflict.IsError == true);
        Assert.StartsWith("conflict:", RawText(conflict));
    }
}
