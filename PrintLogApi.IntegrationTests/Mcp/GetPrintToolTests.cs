using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    public class GetPrintToolTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private const string ToolName = "get_print";
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly McpDataWebApplicationFactory _factory;

        public GetPrintToolTests(McpDataWebApplicationFactory factory) => _factory = factory;

        private sealed record Detail(long Id, string Title, string Status, long? PrinterId,
            string PrinterName, DateTimeOffset? StartedAt, double MaterialUsedGrams, int? DurationSeconds,
            decimal? EstimatedCost, string Notes, string ProjectName);

        private static (Detail detail, string rawJson) Parse(CallToolResult result)
        {
            var text = result.Content.OfType<TextContentBlock>().First().Text;
            return (JsonSerializer.Deserialize<Detail>(text, JsonOptions)!, text);
        }

        [Fact]
        public async Task Creator_CanReadOwnPrint()
        {
            await using var client = await _factory.ConnectAsync();
            var result = await client.CallToolAsync(ToolName, new Dictionary<string, object> { ["id"] = McpTestData.RichPrintId1 });
            var (detail, _) = Parse(result);

            Assert.Equal(McpTestData.RichPrintId1, detail.Id);
            Assert.Equal("Rich Print 1", detail.Title);
            Assert.Equal("Success", detail.Status);
            Assert.Equal(25.0, detail.MaterialUsedGrams);
            Assert.Equal(7200, detail.DurationSeconds);
        }

        [Fact]
        public async Task Caller_CannotReadForeignPublicPrint()
        {
            // ForeignPrintId is Public but owned by another user: creator-only => not found.
            await using var client = await _factory.ConnectAsync();
            Assert.True(await McpDataWebApplicationFactory.IsToolError(
                client, ToolName, new() { ["id"] = McpTestData.ForeignPrintId }));
        }

        [Fact]
        public async Task MissingPrint_IsError()
        {
            await using var client = await _factory.ConnectAsync();
            Assert.True(await McpDataWebApplicationFactory.IsToolError(
                client, ToolName, new() { ["id"] = 999999L }));
        }

        [Fact]
        public async Task Detail_ExcludesImagesCommentsAndFiles()
        {
            await using var client = await _factory.ConnectAsync();
            var result = await client.CallToolAsync(ToolName, new Dictionary<string, object> { ["id"] = McpTestData.RichPrintId1 });
            var (_, rawJson) = Parse(result);

            foreach (var forbidden in new[] { "image", "comment", "fileName", "\"url\"", "fileHash", "attachment" })
            {
                Assert.DoesNotContain(forbidden, rawJson, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
