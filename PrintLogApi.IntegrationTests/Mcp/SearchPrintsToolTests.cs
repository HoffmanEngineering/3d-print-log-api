using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    public class SearchPrintsToolTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private const string ToolName = "search_prints";
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly McpDataWebApplicationFactory _factory;

        public SearchPrintsToolTests(McpDataWebApplicationFactory factory) => _factory = factory;

        private sealed record Item(long Id, string Title, string Status, long? PrinterId,
            string PrinterName, DateTimeOffset? StartedAt, double MaterialUsedGrams, int? DurationSeconds);

        private sealed record PageResult(List<Item> Items, int Page, int PageSize, int TotalCount, int TotalPages);

        private static (PageResult page, string rawJson) ParsePage(CallToolResult result)
        {
            var text = result.Content.OfType<TextContentBlock>().First().Text;
            return (JsonSerializer.Deserialize<PageResult>(text, JsonOptions)!, text);
        }

        private static async Task<CallToolResult> Search(McpClient client, Dictionary<string, object> args) =>
            await client.CallToolAsync(ToolName, args);

        [Fact]
        public async Task CallerOnly_ExcludesForeignPrints()
        {
            await using var client = await _factory.ConnectAsync();
            var (page, _) = ParsePage(await Search(client, new() { ["pageSize"] = 100 }));

            Assert.DoesNotContain(page.Items, i => i.Id == McpTestData.ForeignPrintId);
            Assert.DoesNotContain(page.Items, i => i.Title == "FOREIGN PRINT");
        }

        [Fact]
        public async Task FilterByPrinter_ReturnsOnlyThatPrinterNewestFirst()
        {
            await using var client = await _factory.ConnectAsync();
            var (page, _) = ParsePage(await Search(client,
                new() { ["printerId"] = IntegrationTestSeeder.TestPrinterId2, ["pageSize"] = 100 }));

            Assert.All(page.Items, i => Assert.Equal(IntegrationTestSeeder.TestPrinterId2, i.PrinterId));
            Assert.Equal(new[] { McpTestData.RichPrintId2, McpTestData.RichPrintId1 },
                page.Items.Select(i => i.Id).ToArray());
        }

        [Fact]
        public async Task FilterByStatus_ReturnsMatching()
        {
            await using var client = await _factory.ConnectAsync();
            var (page, _) = ParsePage(await Search(client,
                new() { ["status"] = Print.PrintStatus.Failed, ["pageSize"] = 100 }));

            Assert.Equal(new[] { McpTestData.RichPrintId2 }, page.Items.Select(i => i.Id).ToArray());
            Assert.All(page.Items, i => Assert.Equal("Failed", i.Status));
        }

        [Fact]
        public async Task FilterByMaterial_ReturnsMatching()
        {
            await using var client = await _factory.ConnectAsync();
            var (page, _) = ParsePage(await Search(client,
                new() { ["materialId"] = IntegrationTestSeeder.TestFilamentId1, ["pageSize"] = 100 }));

            Assert.Equal(new[] { McpTestData.RichPrintId1 }, page.Items.Select(i => i.Id).ToArray());
        }

        [Fact]
        public async Task Result_UsesGramsAndSeconds_AndExcludesNotes()
        {
            await using var client = await _factory.ConnectAsync();
            var (page, rawJson) = ParsePage(await Search(client,
                new() { ["printerId"] = IntegrationTestSeeder.TestPrinterId2, ["pageSize"] = 100 }));

            var rich1 = page.Items.Single(i => i.Id == McpTestData.RichPrintId1);
            Assert.Equal(25.0, rich1.MaterialUsedGrams);
            Assert.Equal(7200, rich1.DurationSeconds);

            // Sensitive/omitted fields must not appear in the serialized payload.
            Assert.DoesNotContain("secret notes", rawJson);
            Assert.DoesNotContain("\"notes\"", rawJson, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task PageSize_ClampsTo100()
        {
            await using var client = await _factory.ConnectAsync();
            var (page, _) = ParsePage(await Search(client, new() { ["pageSize"] = 1000 }));
            Assert.Equal(100, page.PageSize);
        }

        [Fact]
        public async Task SecondPage_IsStableAndDisjoint()
        {
            await using var client = await _factory.ConnectAsync();
            var (p1, _) = ParsePage(await Search(client, new() { ["page"] = 1, ["pageSize"] = 2 }));
            var (p2, _) = ParsePage(await Search(client, new() { ["page"] = 2, ["pageSize"] = 2 }));

            Assert.Equal(2, p1.Items.Count);
            Assert.Equal(2, p2.Items.Count);
            Assert.Empty(p1.Items.Select(i => i.Id).Intersect(p2.Items.Select(i => i.Id)));
        }

        [Fact]
        public async Task Page0_IsError()
        {
            await using var client = await _factory.ConnectAsync();
            Assert.True(await McpDataWebApplicationFactory.IsToolError(client, ToolName, new() { ["page"] = 0 }));
        }

        [Fact]
        public async Task InvalidDateRange_IsError()
        {
            await using var client = await _factory.ConnectAsync();
            var from = DateTimeOffset.UtcNow;
            var to = from.AddDays(-2);
            Assert.True(await McpDataWebApplicationFactory.IsToolError(client, ToolName,
                new() { ["from"] = from, ["to"] = to }));
        }
    }
}
