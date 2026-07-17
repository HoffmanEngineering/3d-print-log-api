using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    /// <summary>
    /// End-to-end checks over the real /mcp endpoint: tool naming, annotations, scope visibility, and
    /// the guarantee that a WRITE-ONLY token can verify everything it wrote from the tool's own
    /// response — without ever holding the read scope.
    /// </summary>
    public class WriteToolsProtocolTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly McpDataWebApplicationFactory _factory;
        public WriteToolsProtocolTests(McpDataWebApplicationFactory factory) => _factory = factory;

        private static readonly string[] ReadWrite = { "read:printdata", "write:printdata" };
        private static readonly string[] WriteOnly = { "write:printdata" };
        private static readonly string[] ReadOnly = { "read:printdata" };

        private static string RawText(CallToolResult result) =>
            result.Content.OfType<TextContentBlock>().First().Text;

        [Fact]
        public async Task ToolList_ExposesRenamedTools_WithAnnotations()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
            var tools = await client.ListToolsAsync();

            Assert.DoesNotContain(tools, t => t.Name == "log_print");

            var create = Assert.Single(tools.Where(t => t.Name == "create_print"));
            Assert.True(create.ProtocolTool.Annotations?.IdempotentHint);

            var update = Assert.Single(tools.Where(t => t.Name == "update_print"));
            Assert.True(update.ProtocolTool.Annotations?.DestructiveHint);
        }

        [Fact]
        public async Task ReadOnlyToken_CannotSeeWriteTools()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadOnly);
            var names = (await client.ListToolsAsync()).Select(t => t.Name).ToHashSet();
            Assert.DoesNotContain("create_print", names);
            Assert.DoesNotContain("update_print", names);
        }

        [Fact]
        public async Task WriteOnlyToken_ReadsBackEveryFieldFromTheToolResult()
        {
            // The point of returning full detail: an agent holding ONLY write:printdata must be able to
            // confirm what it wrote without get_print (which it cannot call).
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, WriteOnly);

            var result = await client.CallToolAsync("create_print", new Dictionary<string, object>
            {
                ["title"] = "protocol-fields",
                ["printerId"] = McpTestData.SearchPrinterId,
                ["status"] = "Success",
                ["idempotencyKey"] = "proto-fields",
                ["estimatedDurationSeconds"] = 3300,
                ["fileName"] = "proto.gcode",
                ["url"] = "https://example.com/proto",
                ["viewStatus"] = "Unlisted",
                ["allowComments"] = true,
                ["allowFileDownloads"] = true,
            });
            Assert.True(result.IsError != true);

            using var doc = JsonDocument.Parse(RawText(result));
            var print = doc.RootElement.GetProperty("print");
            Assert.Equal("proto.gcode", print.GetProperty("fileName").GetString());
            Assert.Equal("https://example.com/proto", print.GetProperty("url").GetString());
            Assert.Equal("Unlisted", print.GetProperty("viewStatus").GetString());
            Assert.Equal(3300, print.GetProperty("estimatedDurationSeconds").GetInt32());
            Assert.True(print.GetProperty("allowComments").GetBoolean());
            Assert.True(print.GetProperty("allowFileDownloads").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("wasReplayed").GetBoolean());
        }

        [Fact]
        public async Task ReusedKeyWithDifferentArguments_SurfacesConflict()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            Dictionary<string, object> Args(string title) => new()
            {
                ["title"] = title,
                ["printerId"] = McpTestData.SearchPrinterId,
                ["status"] = "Success",
                ["idempotencyKey"] = "proto-conflict",
            };

            var first = await client.CallToolAsync("create_print", Args("original"));
            Assert.True(first.IsError != true);

            var replay = await client.CallToolAsync("create_print", Args("original"));
            Assert.True(replay.IsError != true);
            using (var replayDoc = JsonDocument.Parse(RawText(replay)))
            {
                Assert.True(replayDoc.RootElement.GetProperty("wasReplayed").GetBoolean());
            }

            var conflict = await client.CallToolAsync("create_print", Args("CHANGED"));
            Assert.True(conflict.IsError == true);
            Assert.StartsWith("conflict:", RawText(conflict));
        }
    }
}
