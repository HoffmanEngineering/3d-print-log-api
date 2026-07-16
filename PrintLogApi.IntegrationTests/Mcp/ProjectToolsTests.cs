using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    /// <summary>End-to-end tests for list_projects, create_project, and update_project.</summary>
    public class ProjectToolsTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private readonly McpDataWebApplicationFactory _factory;

        public ProjectToolsTests(McpDataWebApplicationFactory factory) => _factory = factory;

        private static readonly string[] ReadWrite = { "read:printdata", "write:printdata" };

        [Fact]
        public async Task ListProjects_ReturnsOwnBySearch_ExcludesForeign()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            var result = await client.CallToolAsync("list_projects", new Dictionary<string, object>
            {
                ["search"] = "Rocket",
            });

            var text = result.Content.OfType<TextContentBlock>().First().Text;
            Assert.Contains("Rocket Build", text);
            Assert.DoesNotContain("SECRET FOREIGN PROJECT", text);
        }

        [Fact]
        public async Task CreateProject_EchoesResultingVisibility()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            var result = await client.CallToolAsync("create_project", new Dictionary<string, object>
            {
                ["name"] = "Agent Created Project",
                ["viewStatus"] = "Unlisted",
            });

            Assert.True(result.IsError != true);
            var text = result.Content.OfType<TextContentBlock>().First().Text;
            Assert.Contains("Unlisted", text);
        }

        [Fact]
        public async Task UpdateProject_ForeignProject_ReturnsNotFound()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            var code = await McpDataWebApplicationFactory.ToolErrorCode(client, "update_project",
                new Dictionary<string, object>
                {
                    ["id"] = McpTestData.ForeignProjectId,
                    ["name"] = "hijack",
                });

            Assert.Equal("not_found", code);
        }
    }
}
