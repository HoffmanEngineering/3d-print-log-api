using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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

            var result = await client.CallToolAsync("list_projects", new Dictionary<string, object?>
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

            var result = await client.CallToolAsync("create_project", new Dictionary<string, object?>
            {
                ["name"] = "Agent Created Project",
                ["viewStatus"] = "Unlisted",
            });

            Assert.True(result.IsError != true);
            var text = result.Content.OfType<TextContentBlock>().First().Text;
            Assert.Contains("Unlisted", text);
        }

        // There is no get_project, so the create/update echo is the ONLY way a caller can confirm what
        // it wrote. Echoing name and status but not the three fields it just set made that impossible.
        [Fact]
        public async Task CreateProject_EchoesEverySettableField()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            var result = await client.CallToolAsync("create_project", new Dictionary<string, object?>
            {
                ["name"] = "Echo Everything",
                ["reference"] = "REF-42",
                ["description"] = "the full description",
                ["url"] = "https://example.com/thing",
                ["status"] = "Complete",
                ["viewStatus"] = "Public",
            });

            Assert.True(result.IsError != true);
            using var doc = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().First().Text);
            var project = doc.RootElement.GetProperty("project");
            Assert.Equal("Echo Everything", project.GetProperty("name").GetString());
            Assert.Equal("REF-42", project.GetProperty("reference").GetString());
            Assert.Equal("the full description", project.GetProperty("description").GetString());
            Assert.Equal("https://example.com/thing", project.GetProperty("url").GetString());
            Assert.Equal("Complete", project.GetProperty("status").GetString());
            Assert.Equal("Public", project.GetProperty("viewStatus").GetString());
            Assert.False(doc.RootElement.GetProperty("wasReplayed").GetBoolean());
        }

        [Fact]
        public async Task UpdateProject_EchoesEverySettableField()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            var created = await client.CallToolAsync("create_project", new Dictionary<string, object?>
            {
                ["name"] = "Update Echo Target",
            });
            Guid id;
            using (var doc = JsonDocument.Parse(created.Content.OfType<TextContentBlock>().First().Text))
            {
                id = doc.RootElement.GetProperty("project").GetProperty("projectId").GetGuid();
            }

            var updated = await client.CallToolAsync("update_project", new Dictionary<string, object?>
            {
                ["id"] = id,
                ["reference"] = "REF-99",
                ["url"] = "https://example.com/updated",
            });

            Assert.True(updated.IsError != true);
            using var updatedDoc = JsonDocument.Parse(updated.Content.OfType<TextContentBlock>().First().Text);
            // update_project returns the project unwrapped — it has nothing to replay.
            Assert.Equal("REF-99", updatedDoc.RootElement.GetProperty("reference").GetString());
            Assert.Equal("https://example.com/updated", updatedDoc.RootElement.GetProperty("url").GetString());
            Assert.Equal("Update Echo Target", updatedDoc.RootElement.GetProperty("name").GetString());
        }

        // Without a key a retried create silently duplicates — the other three create tools all take
        // one, and the dev database already collected two projects both named "Test Project 1".
        [Fact]
        public async Task CreateProject_SameKeyAndArguments_Replays()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            Dictionary<string, object?> Args() => new()
            {
                ["name"] = "Idempotent Project",
                ["idempotencyKey"] = "proj-key-1",
            };

            var first = await client.CallToolAsync("create_project", Args());
            Assert.True(first.IsError != true);
            var replay = await client.CallToolAsync("create_project", Args());
            Assert.True(replay.IsError != true);

            using var firstDoc = JsonDocument.Parse(first.Content.OfType<TextContentBlock>().First().Text);
            using var replayDoc = JsonDocument.Parse(replay.Content.OfType<TextContentBlock>().First().Text);

            Assert.False(firstDoc.RootElement.GetProperty("wasReplayed").GetBoolean());
            Assert.True(replayDoc.RootElement.GetProperty("wasReplayed").GetBoolean());
            Assert.Equal(
                firstDoc.RootElement.GetProperty("project").GetProperty("projectId").GetGuid(),
                replayDoc.RootElement.GetProperty("project").GetProperty("projectId").GetGuid());
        }

        [Fact]
        public async Task CreateProject_SameKeyDifferentArguments_Conflicts()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            await client.CallToolAsync("create_project", new Dictionary<string, object?>
            {
                ["name"] = "Conflict Project",
                ["idempotencyKey"] = "proj-key-2",
            });

            var conflict = await client.CallToolAsync("create_project", new Dictionary<string, object?>
            {
                ["name"] = "Conflict Project CHANGED",
                ["idempotencyKey"] = "proj-key-2",
            });

            // Asserted on the raw prefix, not via ToolErrorCode: that helper only recognizes
            // "not found" and "denied", and reports every other failure as a bare "error" — it can
            // never distinguish a conflict from a crash.
            Assert.True(conflict.IsError == true);
            Assert.StartsWith("conflict:", conflict.Content.OfType<TextContentBlock>().First().Text);
        }

        // Same contract as create_material/create_printer: the key is optional, and without one a
        // retry is a second project. Pinned so the residual risk stays a documented property.
        [Fact]
        public async Task CreateProject_WithoutKey_CreatesASecondProject()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            Dictionary<string, object?> Args() => new() { ["name"] = "Duplicate Project" };

            var first = await client.CallToolAsync("create_project", Args());
            var second = await client.CallToolAsync("create_project", Args());

            using var firstDoc = JsonDocument.Parse(first.Content.OfType<TextContentBlock>().First().Text);
            using var secondDoc = JsonDocument.Parse(second.Content.OfType<TextContentBlock>().First().Text);
            Assert.NotEqual(
                firstDoc.RootElement.GetProperty("project").GetProperty("projectId").GetGuid(),
                secondDoc.RootElement.GetProperty("project").GetProperty("projectId").GetGuid());
        }

        [Fact]
        public async Task UpdateProject_ForeignProject_ReturnsNotFound()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            var code = await McpDataWebApplicationFactory.ToolErrorCode(client, "update_project",
                new Dictionary<string, object?>
                {
                    ["id"] = McpTestData.ForeignProjectId,
                    ["name"] = "hijack",
                });

            Assert.Equal("not_found", code);
        }
    }
}
