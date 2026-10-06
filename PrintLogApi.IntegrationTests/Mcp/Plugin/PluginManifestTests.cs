using System.Text.Json;
using System.Text.Json.Nodes;
using NJsonSchema;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp.Plugin;

/// <summary>
/// The repo root is an installable plugin that bundles the MCP server with the agent skill
/// (UI #215), described twice because no client reads both formats:
/// <list type="bullet">
/// <item><c>plugin.json</c> + <c>mcp.json</c>: Agent Plugins 1.0.0 (agent-plugins.org, published
/// 2026; spec and schemas at github.com/agentplugins/agent-plugins-spec). The schemas are
/// vendored here so validation needs no network, as <see cref="Registry.ServerJsonTests"/> does
/// for server.json.</item>
/// <item><c>.claude-plugin/marketplace.json</c>: a Claude Code marketplace whose one entry is the
/// repo root. Agent Plugins has no OAuth fields, and the server does not support dynamic client
/// registration, so this is the format that can carry the public client ID.</item>
/// </list>
/// Both point at the same endpoint as <c>server.json</c>, and these tests keep it that way.
/// </summary>
public class PluginManifestTests
{
    private static readonly string Directory = Path.Combine(AppContext.BaseDirectory, "Mcp", "Plugin");

    private const string PluginSchemaFile = "plugin.schema.1.0.0.json";
    private const string McpSchemaFile = "mcp.schema.1.0.0.json";

    private const string McpUrl = "https://api.3dprintlog.com/mcp";

    /// <summary>The public Auth0 client every MCP client uses; see the UI's /docs/mcp and /auth.md.</summary>
    private const string PublicClientId = "uzxvtpefYIrWoYbaJteoRzZtIYw4wP7j";

    private static string Read(string name) => File.ReadAllText(Path.Combine(Directory, name));

    private static JsonElement Parse(string name) => JsonDocument.Parse(Read(name)).RootElement.Clone();

    /// <summary>
    /// The schemas are draft 2020-12, and NJsonSchema 11 misreads two of its keywords. It does not
    /// resolve <c>$defs</c> (it throws "reference path '#/$defs/...' has not been resolved"), and
    /// it ignores <c>const</c>, so the transport variants under <c>oneOf</c> stop being exclusive
    /// and a valid server matches two of them. Both have exact older spellings: <c>definitions</c>,
    /// and a one-value <c>enum</c>. The vendored files stay byte-identical to the published ones
    /// and are rewritten here, in memory only.
    /// </summary>
    private static Task<JsonSchema> LoadSchemaAsync(string file)
    {
        var schema = JsonNode.Parse(Read(file))!;
        Downlevel(schema);
        return JsonSchema.FromJsonAsync(schema.ToJsonString(), TestContext.Current.CancellationToken);
    }

    private static void Downlevel(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                Downlevel(item);
            }
            return;
        }
        if (node is not JsonObject obj)
        {
            return;
        }
        foreach (var (key, value) in obj.ToArray())
        {
            switch (key)
            {
                case "$defs":
                    obj.Remove(key);
                    obj["definitions"] = value;
                    break;
                case "const":
                    obj.Remove(key);
                    obj["enum"] = new JsonArray(value);
                    break;
                case "$ref" when value!.GetValue<string>().StartsWith("#/$defs/", StringComparison.Ordinal):
                    obj[key] = "#/definitions/" + value.GetValue<string>()["#/$defs/".Length..];
                    break;
            }
            Downlevel(obj[key == "$defs" ? "definitions" : key == "const" ? "enum" : key]);
        }
    }

    private static string Describe(ICollection<NJsonSchema.Validation.ValidationError> errors) =>
        string.Join("\n", errors.Select(e => $"{e.Path}: {e.Kind}"));

    [Theory]
    [InlineData("plugin.json", PluginSchemaFile)]
    [InlineData("mcp.json", McpSchemaFile)]
    public async Task Manifest_IsValidAgainstTheAgentPluginsSchema(string manifest, string schemaFile)
    {
        var schema = await LoadSchemaAsync(schemaFile);

        var errors = schema.Validate(Read(manifest));

        Assert.True(errors.Count == 0, $"{manifest} violates {schemaFile}:\n{Describe(errors)}");
    }

    [Theory]
    [InlineData("plugin.json", PluginSchemaFile)]
    [InlineData("mcp.json", McpSchemaFile)]
    public void DeclaredSchema_IsTheVendoredSchema(string manifest, string schemaFile)
    {
        Assert.Equal(
            Parse(schemaFile).GetProperty("$id").GetString(),
            Parse(manifest).GetProperty("$schema").GetString());
    }

    /// <summary>
    /// Proves the validator enforces the 2020-12 keywords these schemas rely on (a lookahead
    /// pattern, a closed object, <c>const</c>, <c>$defs</c> with <c>oneOf</c>). A library that skipped them would
    /// make the test above pass for anything.
    /// </summary>
    [Fact]
    public async Task Schemas_RejectWhatAgentPluginsForbids()
    {
        var pluginSchema = await LoadSchemaAsync(PluginSchemaFile);
        var plugin = JsonNode.Parse(Read("plugin.json"))!;
        plugin["name"] = "3d--print-log";
        Assert.NotEmpty(pluginSchema.Validate(plugin.ToJsonString()));
        plugin = JsonNode.Parse(Read("plugin.json"))!;
        plugin["mcpServers"] = new JsonObject();
        Assert.NotEmpty(pluginSchema.Validate(plugin.ToJsonString()));
        plugin = JsonNode.Parse(Read("plugin.json"))!;
        plugin["$schema"] = "https://agent-plugins.org/schemas/1.1.0/plugin.schema.json";
        Assert.NotEmpty(pluginSchema.Validate(plugin.ToJsonString()));

        var mcpSchema = await LoadSchemaAsync(McpSchemaFile);
        var mcp = JsonNode.Parse(Read("mcp.json"))!;
        // Claude Code's spelling of the transport, which Agent Plugins does not accept.
        mcp["mcpServers"]!["printlog"]!["type"] = "http";
        Assert.NotEmpty(mcpSchema.Validate(mcp.ToJsonString()));
    }

    [Fact]
    public void PluginName_IsTheSkillName()
    {
        Assert.Equal(AgentSkillTests.SkillName, Parse("plugin.json").GetProperty("name").GetString());
    }

    /// <summary>
    /// One remote: the production endpoint server.json lists, with no headers. Clients discover
    /// OAuth from the 401, and Agent Plugins forbids credentials in headers.
    /// </summary>
    [Fact]
    public void McpJson_IsTheRegistryEndpointOnly()
    {
        var servers = Parse("mcp.json").GetProperty("mcpServers").EnumerateObject().ToArray();
        var server = Assert.Single(servers).Value;

        Assert.Equal("streamable-http", server.GetProperty("type").GetString());
        Assert.Equal(McpUrl, server.GetProperty("url").GetString());
        Assert.False(server.TryGetProperty("headers", out _));
        var registryRemote = Assert.Single(Parse("server.json").GetProperty("remotes").EnumerateArray().ToArray());
        Assert.Equal(registryRemote.GetProperty("url").GetString(), server.GetProperty("url").GetString());
    }

    private static JsonElement MarketplaceEntry()
    {
        var marketplace = Parse("marketplace.json");
        Assert.Equal("3d-print-log", marketplace.GetProperty("name").GetString());
        return Assert.Single(marketplace.GetProperty("plugins").EnumerateArray().ToArray());
    }

    /// <summary>
    /// The Claude Code entry repeats what plugin.json and mcp.json say, so the two descriptions of
    /// one plugin cannot drift apart. Its server pre-sets the public client ID and a loopback
    /// port from the pre-registered 8400 to 8405 range, which is what lets it sign in at all.
    /// </summary>
    [Fact]
    public void MarketplaceEntry_MatchesTheAgentPluginsManifest()
    {
        var entry = MarketplaceEntry();
        var plugin = Parse("plugin.json");

        Assert.Equal(plugin.GetProperty("name").GetString(), entry.GetProperty("name").GetString());
        Assert.Equal(plugin.GetProperty("description").GetString(), entry.GetProperty("description").GetString());
        Assert.Equal("./", entry.GetProperty("source").GetString());

        var server = Assert.Single(entry.GetProperty("mcpServers").EnumerateObject().ToArray());
        Assert.Equal("printlog", server.Name);
        Assert.Equal("http", server.Value.GetProperty("type").GetString());
        Assert.Equal(McpUrl, server.Value.GetProperty("url").GetString());
        var oauth = server.Value.GetProperty("oauth");
        Assert.Equal(PublicClientId, oauth.GetProperty("clientId").GetString());
        Assert.InRange(oauth.GetProperty("callbackPort").GetInt32(), 8400, 8405);
    }

    /// <summary>
    /// An entry whose source is the marketplace root loads only the skills it lists, so a new
    /// skill directory that is not listed would silently never reach a Claude Code user.
    /// </summary>
    [Fact]
    public void MarketplaceEntry_ListsEverySkillDirectory()
    {
        var listed = MarketplaceEntry().GetProperty("skills").EnumerateArray()
            .Select(s => s.GetString()!)
            .ToArray();
        var onDisk = System.IO.Directory.GetDirectories(AgentSkillTests.SkillsDirectory)
            .Select(d => $"./skills/{Path.GetFileName(d)}")
            .ToArray();

        Assert.Equal(onDisk.OrderBy(s => s), listed.OrderBy(s => s));
    }
}
