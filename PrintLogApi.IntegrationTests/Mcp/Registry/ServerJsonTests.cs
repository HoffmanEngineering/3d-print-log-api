using System.Text.Json;
using System.Text.RegularExpressions;
using NJsonSchema;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp.Registry;

/// <summary>
/// <c>server.json</c> at the repo root is what the release workflow publishes to the official MCP
/// Registry (#128, docs/mcp-registry-listing.md). The registry validates it only at publish time,
/// which is the release, after production has already been deployed. These tests move that check
/// to every PR, against a vendored copy of the schema, so they need no network.
///
/// The first draft of this file failed exactly this way: its 230-character description passed
/// every review and would have been rejected by the registry's 100-character limit.
/// </summary>
public class ServerJsonTests
{
    private static readonly string Directory = Path.Combine(AppContext.BaseDirectory, "Mcp", "Registry");
    private static readonly string ServerJsonPath = Path.Combine(Directory, "server.json");

    /// <summary>
    /// The schema version <c>server.json</c> declares. Bumping <c>$schema</c> means vendoring the
    /// matching file next to the old one and changing this constant;
    /// <see cref="DeclaredSchema_IsTheVendoredSchema"/> fails until both are done.
    /// </summary>
    private const string SchemaFile = "server.schema.2025-12-11.json";

    private static string ReadServerJson() => File.ReadAllText(ServerJsonPath);

    private static JsonElement ParseServerJson() => JsonDocument.Parse(ReadServerJson()).RootElement.Clone();

    private static Task<JsonSchema> LoadSchemaAsync() =>
        JsonSchema.FromJsonAsync(File.ReadAllText(Path.Combine(Directory, SchemaFile)), TestContext.Current.CancellationToken);

    [Fact]
    public async Task ServerJson_IsValidAgainstTheRegistrySchema()
    {
        var schema = await LoadSchemaAsync();

        var errors = schema.Validate(ReadServerJson());

        Assert.True(errors.Count == 0, "server.json violates the registry schema:\n" +
            string.Join("\n", errors.Select(e => $"{e.Path}: {e.Kind}")));
    }

    /// <summary>
    /// Proves the validator above actually enforces the limit that bit us. A schema library that
    /// silently skipped keywords would make the test above pass for anything.
    /// </summary>
    [Fact]
    public async Task Schema_RejectsADescriptionOverTheRegistryLimit()
    {
        var schema = await LoadSchemaAsync();
        var node = System.Text.Json.Nodes.JsonNode.Parse(ReadServerJson())!;
        node["description"] = new string('x', 101);

        var errors = schema.Validate(node.ToJsonString());

        Assert.Contains(errors, e => e.Path == "#/description");
    }

    [Fact]
    public void DeclaredSchema_IsTheVendoredSchema()
    {
        var vendored = JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory, SchemaFile))).RootElement;

        Assert.Equal(
            vendored.GetProperty("$id").GetString(),
            ParseServerJson().GetProperty("$schema").GetString());
    }

    /// <summary>
    /// DNS authentication grants the reverse-DNS of the domain whose apex carries the TXT record,
    /// so the namespace must be exactly <c>com.3dprintlog</c>. The name itself is public and
    /// permanent once published, and the UI's MCP server card
    /// (<c>/.well-known/mcp/server-card.json</c>) mirrors it, so it is pinned here rather than
    /// left to drift.
    /// </summary>
    [Fact]
    public void Name_IsInTheDomainVerifiedNamespace()
    {
        Assert.Equal("com.3dprintlog/printlog", ParseServerJson().GetProperty("name").GetString());
    }

    /// <summary>
    /// A remote-only listing: one Streamable HTTP endpoint, and no packages (there is no stdio
    /// distribution) and no SSE (deprecated, and the server is stateless Streamable HTTP only).
    /// No headers either — clients discover OAuth from the 401, never from a configured key.
    /// </summary>
    [Fact]
    public void Remotes_AreTheProductionStreamableHttpEndpointOnly()
    {
        var root = ParseServerJson();

        Assert.False(root.TryGetProperty("packages", out _));
        var remote = Assert.Single(root.GetProperty("remotes").EnumerateArray().ToArray());
        Assert.Equal("streamable-http", remote.GetProperty("type").GetString());
        Assert.Equal("https://api.3dprintlog.com/mcp", remote.GetProperty("url").GetString());
        Assert.False(remote.TryGetProperty("headers", out _));
    }

    /// <summary>
    /// The registry accepts any version string, but sorts "latest" by semver and refuses to
    /// republish a version it already holds. A plain MAJOR.MINOR.PATCH keeps both predictable; a
    /// prerelease suffix would sort below the release it amends and never become latest.
    /// </summary>
    [Fact]
    public void Version_IsAPlainSemanticVersion()
    {
        var version = ParseServerJson().GetProperty("version").GetString()!;

        Assert.Matches(new Regex(@"^\d{1,9}\.\d{1,9}\.\d{1,9}$"), version);
    }
}
