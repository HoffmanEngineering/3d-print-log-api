using System.Text.Json;
using System.Text.RegularExpressions;
using PrintLogApi.Mcp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp.Plugin;

/// <summary>
/// <c>skills/3d-print-log/SKILL.md</c> is the agent skill published for 3D Print Log (UI #215):
/// installed by <c>npx skills add HoffmanEngineering/3d-print-log-api</c>, bundled by the plugin
/// manifests at the repo root, and mirrored byte for byte at
/// <c>https://www.3dprintlog.com/.well-known/agent-skills/3d-print-log/SKILL.md</c>.
///
/// An agent follows a skill literally, so a renamed tool or parameter turns a correct instruction
/// into a failing call that nothing else in this repo would notice. These tests read the skill
/// against the live <c>tools/list</c>: every tool it names must exist, and every argument it puts
/// in a call must be one that tool takes.
/// </summary>
public partial class AgentSkillTests : IClassFixture<McpDataWebApplicationFactory>
{
    private static readonly string[] ReadWrite = { "read:printdata", "write:printdata" };

    internal static readonly string SkillsDirectory =
        Path.Combine(AppContext.BaseDirectory, "Mcp", "Plugin", "skills");

    internal const string SkillName = "3d-print-log";

    private static string SkillPath => Path.Combine(SkillsDirectory, SkillName, "SKILL.md");

    /// <summary>Error codes are snake_case too; the skill names them so an agent can react.</summary>
    private static readonly HashSet<string> ErrorCodes = new[]
    {
        McpToolException.NotFound(), McpToolException.InvalidArguments("x"),
        McpToolException.Forbidden(), McpToolException.Conflict("x"),
        McpToolException.Unavailable("x"),
    }.Select(e => e.Code).ToHashSet();

    private readonly McpDataWebApplicationFactory _factory;

    public AgentSkillTests(McpDataWebApplicationFactory factory) => _factory = factory;

    /// <summary>A tool name is snake_case with at least one underscore; parameters are camelCase.</summary>
    [GeneratedRegex(@"^[a-z]+(?:_[a-z]+)+$")]
    private static partial Regex ToolName();

    /// <summary>Inline code spans. A span never contains a backtick, so there is one way to match.</summary>
    [GeneratedRegex(@"`([^`\n]+)`")]
    private static partial Regex CodeSpan();

    /// <summary><c>tool_name(arg, arg)</c> inside a code span.</summary>
    [GeneratedRegex(@"^([a-z_]+)\(([^()]*)\)$")]
    private static partial Regex ToolCall();

    /// <summary><c>{ field, field }</c> inside a code span: a material usage row.</summary>
    [GeneratedRegex(@"^\{([^{}]*)\}$")]
    private static partial Regex UsageRow();

    /// <summary>Agent Skills name rule: lowercase alphanumerics and single hyphens, 1 to 64.</summary>
    [GeneratedRegex(@"^(?!.*--)[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$")]
    private static partial Regex SkillNameRule();

    internal static (Dictionary<string, string> Frontmatter, string Body) ReadSkill(string path)
    {
        var text = File.ReadAllText(path);
        Assert.StartsWith("---\n", text);
        var end = text.IndexOf("\n---\n", 3, StringComparison.Ordinal);
        Assert.True(end > 0, $"{path} has no closing frontmatter fence");

        // Only the top-level `key: value` lines; `metadata` is a nested map and is checked by key.
        var frontmatter = new Dictionary<string, string>();
        foreach (var line in text[4..end].Split('\n'))
        {
            if (line.Length == 0 || line[0] == ' ')
            {
                continue;
            }
            var colon = line.IndexOf(':');
            Assert.True(colon > 0, $"unexpected frontmatter line: {line}");
            frontmatter[line[..colon]] = line[(colon + 1)..].Trim();
        }
        return (frontmatter, text[(end + 5)..]);
    }

    private static IEnumerable<string> CodeSpans() =>
        CodeSpan().Matches(ReadSkill(SkillPath).Body).Select(m => m.Groups[1].Value.Trim());

    private async Task<Dictionary<string, JsonElement>> ListToolSchemasAsync()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        return tools.ToDictionary(t => t.Name, t => t.ProtocolTool.InputSchema);
    }

    private static HashSet<string> PropertyNames(JsonElement schema) =>
        schema.TryGetProperty("properties", out var properties)
            ? properties.EnumerateObject().Select(p => p.Name).ToHashSet()
            : new HashSet<string>();

    /// <summary>
    /// The Agent Skills specification (agentskills.io/specification): <c>name</c> matches the
    /// directory and the naming rule, <c>description</c> is 1 to 1024 characters, and only the
    /// fields the spec defines appear. The description stays on one line because the UI's
    /// discovery index copies it verbatim and its reader handles plain scalars only.
    /// </summary>
    [Fact]
    public void EverySkill_HasValidAgentSkillsFrontmatter()
    {
        var directories = Directory.GetDirectories(SkillsDirectory);
        Assert.NotEmpty(directories);

        foreach (var directory in directories)
        {
            var (frontmatter, body) = ReadSkill(Path.Combine(directory, "SKILL.md"));
            var name = frontmatter["name"];

            Assert.Equal(Path.GetFileName(directory), name);
            Assert.Matches(SkillNameRule(), name);
            var description = frontmatter["description"];
            Assert.InRange(description.Length, 1, 1024);
            Assert.DoesNotMatch(@"^[>|""']", description);
            Assert.Empty(frontmatter.Keys.Except(
                new[] { "name", "description", "license", "compatibility", "metadata", "allowed-tools" }));
            Assert.False(string.IsNullOrWhiteSpace(body));
        }
    }

    [Fact]
    public async Task EveryToolTheSkillNames_IsALiveTool()
    {
        var tools = await ListToolSchemasAsync();

        var named = CodeSpans()
            .Select(span => ToolCall().Match(span) is { Success: true } call ? call.Groups[1].Value : span)
            .Where(span => ToolName().IsMatch(span) && !ErrorCodes.Contains(span))
            .Distinct()
            .ToArray();

        // Guards the extraction itself: a regex that matched nothing would pass the loop below.
        Assert.Contains("create_print", named);
        Assert.Contains("find_material", named);
        foreach (var tool in named)
        {
            Assert.True(tools.ContainsKey(tool), $"SKILL.md names `{tool}`, which the MCP server does not expose");
        }
    }

    [Fact]
    public async Task EveryArgumentTheSkillPassesToATool_IsAParameterOfThatTool()
    {
        var tools = await ListToolSchemasAsync();

        var calls = CodeSpans().Select(span => ToolCall().Match(span)).Where(m => m.Success).ToArray();
        Assert.NotEmpty(calls);
        foreach (var call in calls)
        {
            var tool = call.Groups[1].Value;
            Assert.True(tools.TryGetValue(tool, out var schema), $"SKILL.md calls unknown tool `{tool}`");
            var parameters = PropertyNames(schema);
            foreach (var argument in call.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                Assert.True(parameters.Contains(argument), $"SKILL.md passes `{argument}` to `{tool}`, which takes: {string.Join(", ", parameters)}");
            }
        }
    }

    [Fact]
    public async Task EveryUsageRowFieldTheSkillShows_IsAFieldOfAMaterialsRow()
    {
        var tools = await ListToolSchemasAsync();
        var rowFields = PropertyNames(tools["create_print"]
            .GetProperty("properties").GetProperty("materials").GetProperty("items"));

        var rows = CodeSpans().Select(span => UsageRow().Match(span)).Where(m => m.Success).ToArray();
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            foreach (var field in row.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                Assert.True(rowFields.Contains(field), $"SKILL.md shows a usage row field `{field}` that create_print does not take");
            }
        }
    }

    /// <summary>
    /// The skill's guardrails promise that nothing can be deleted and that no tool loads or unloads
    /// filament. Those are claims about the whole tool surface, so a new tool that breaks one must
    /// fail here, where the message points at the sentence to rewrite.
    /// </summary>
    [Fact]
    public async Task TheSkillsGuardrails_StillHoldForEveryTool()
    {
        var tools = await ListToolSchemasAsync();
        var body = ReadSkill(SkillPath).Body;
        Assert.Contains("**Nothing can be deleted.**", body);
        Assert.Contains("**Logging a print never changes the loaded filament.**", body);

        foreach (var tool in tools.Keys)
        {
            Assert.False(Regex.IsMatch(tool, "delete|remove|purge|archive"),
                $"`{tool}` may delete; update the guardrails in skills/{SkillName}/SKILL.md");
            Assert.False(Regex.IsMatch(tool, "load"),
                $"`{tool}` may change loaded filament; update the guardrails in skills/{SkillName}/SKILL.md");
        }
    }
}
