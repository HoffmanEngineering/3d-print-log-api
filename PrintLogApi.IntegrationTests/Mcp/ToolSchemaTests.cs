using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp;

/// <summary>
/// The advertised input schema is the only contract an agent can read before calling. These tests
/// pin the places where a generated schema would otherwise overstate what a caller must send.
/// </summary>
public class ToolSchemaTests : IClassFixture<McpDataWebApplicationFactory>
{
    private readonly McpDataWebApplicationFactory _factory;
    public ToolSchemaTests(McpDataWebApplicationFactory factory) => _factory = factory;
    private static readonly string[] ReadWrite = { "read:printdata", "write:printdata" };

    /// <summary>
    /// A material usage row needs only the material id plus at least one complete amount pair,
    /// which the service enforces. The SDK derives 'required' from constructor parameters that
    /// have no default, so a positional record with none marked every field required — including
    /// the nullable ones. The server accepted an omitted 'notes' either way, so the schema was
    /// lying, and an agent reading it sends "notes": null on every row to satisfy a rule that
    /// does not exist.
    /// </summary>
    [Fact]
    public async Task CreatePrint_MaterialRow_RequiresOnlyTheMaterialId()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        var createPrint = tools.Single(t => t.Name == "create_print");
        var required = createPrint.ProtocolTool.InputSchema
            .GetProperty("properties").GetProperty("materials")
            .GetProperty("items").GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Equal(new[] { "materialId" }, required);
    }

    [Fact]
    public async Task UpdatePrint_MaterialRow_RequiresOnlyTheMaterialId()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        var updatePrint = tools.Single(t => t.Name == "update_print");
        var required = updatePrint.ProtocolTool.InputSchema
            .GetProperty("properties").GetProperty("materials")
            .GetProperty("items").GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Equal(new[] { "materialId" }, required);
    }

    /// <summary>
    /// create_feedback is the only create tool whose idempotencyKey is mandatory, and the schema
    /// is where an agent learns that. The same SDK rule applies in reverse here: a parameter is
    /// required precisely because it has no C# default, so giving idempotencyKey one would
    /// silently downgrade it to optional and reintroduce duplicate-notification retries.
    /// </summary>
    [Fact]
    public async Task CreateFeedback_RequiresTypeNoteAndIdempotencyKey()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        var createFeedback = tools.Single(t => t.Name == "create_feedback");
        var required = createFeedback.ProtocolTool.InputSchema
            .GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Equal(new[] { "type", "note", "idempotencyKey" }.OrderBy(x => x), required.OrderBy(x => x));
    }

    /// <summary>
    /// Anthropic's Connectors Directory rejects a server unless EVERY tool carries a title and the
    /// applicable readOnlyHint/destructiveHint, and its submission portal reads them straight off
    /// tools/list. These two tests are the gate: a new tool added with a bare [McpServerTool]
    /// silently disqualifies the whole server from the directory, and nothing else would catch it.
    /// </summary>
    [Fact]
    public async Task EveryTool_AdvertisesATitle()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        var untitled = tools
            .Where(t => string.IsNullOrWhiteSpace(t.ProtocolTool.Title)
                        && string.IsNullOrWhiteSpace(t.ProtocolTool.Annotations?.Title))
            .Select(t => t.Name)
            .ToArray();

        Assert.Empty(untitled);
    }

    /// <summary>
    /// A read tool must say readOnlyHint = true; a write tool must state destructiveHint either
    /// way. Absent annotations are not neutral — a client that cannot tell a read from a
    /// destructive write has to assume the worse of the two and gate the call.
    /// </summary>
    [Fact]
    public async Task EveryTool_DeclaresReadOnlyOrDestructiveIntent()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        var unhinted = tools
            .Where(t => t.ProtocolTool.Annotations is not { } a
                        || (a.ReadOnlyHint != true && !a.DestructiveHint.HasValue))
            .Select(t => t.Name)
            .ToArray();

        Assert.Empty(unhinted);
    }

    /// <summary>
    /// A DateOnly parameter must advertise itself as a date-formatted string. If the SDK emitted
    /// it as a bare object or a full date-time, an agent would send an ISO instant and the civil
    /// date would acquire a time and an offset — the exact drift DateOnly exists to prevent.
    /// </summary>
    [Theory]
    [InlineData("create_project", "startDate")]
    [InlineData("create_project", "finishDate")]
    [InlineData("update_project", "startDate")]
    [InlineData("update_project", "finishDate")]
    public async Task ProjectTools_DateParameters_AreAdvertisedAsDateStrings(string tool, string parameter)
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        var property = tools.Single(t => t.Name == tool)
            .ProtocolTool.InputSchema
            .GetProperty("properties").GetProperty(parameter);

        var json = property.ToString();
        Assert.Contains("string", json);
        Assert.Contains("date", json);
        Assert.DoesNotContain("date-time", json);
    }

    /// <summary>
    /// The clear flags are the only way to go back to an automatic date, so they must be
    /// advertised, and they must be optional — a required flag would force every caller that
    /// only wants to rename a project to state a date policy it has no opinion about.
    /// </summary>
    [Fact]
    public async Task UpdateProject_ClearFlags_AreAdvertisedAndOptional()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        var schema = tools.Single(t => t.Name == "update_project").ProtocolTool.InputSchema;
        var properties = schema.GetProperty("properties");

        Assert.True(properties.TryGetProperty("clearStartDate", out _));
        Assert.True(properties.TryGetProperty("clearFinishDate", out _));

        var required = schema.TryGetProperty("required", out var req)
            ? req.EnumerateArray().Select(e => e.GetString()).ToArray()
            : [];
        Assert.DoesNotContain("clearStartDate", required);
        Assert.DoesNotContain("clearFinishDate", required);
        Assert.DoesNotContain("startDate", required);
        Assert.DoesNotContain("finishDate", required);
    }
}
