using System.Collections.Generic;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    /// <summary>
    /// Proves the read/write scope split: a read-only MCP token is forbidden from a write tool,
    /// while a token carrying write:printdata can invoke one. Uses the trivial whoami write tool so
    /// the policy is exercised independently of any data-mutating tool.
    /// </summary>
    public class McpWritePolicyTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private readonly McpDataWebApplicationFactory _factory;

        public McpWritePolicyTests(McpDataWebApplicationFactory factory) => _factory = factory;

        [Fact]
        public async Task ReadOnlyToken_IsForbiddenFromWriteTool()
        {
            await using var client = await _factory.ConnectAsync(
                IntegrationTestSeeder.TestUserOAuthId, new[] { "read:printdata" });

            var code = await McpDataWebApplicationFactory.ToolErrorCode(
                client, "whoami", new Dictionary<string, object>());

            Assert.Equal("forbidden", code);
        }

        [Fact]
        public async Task WriteToken_CanInvokeWriteTool()
        {
            await using var client = await _factory.ConnectAsync(
                IntegrationTestSeeder.TestUserOAuthId, new[] { "read:printdata", "write:printdata" });

            var result = await client.CallToolAsync("whoami", new Dictionary<string, object>());

            Assert.True(result.IsError != true);
        }
    }
}
