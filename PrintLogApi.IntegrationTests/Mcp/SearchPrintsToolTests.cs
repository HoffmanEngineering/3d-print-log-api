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
            string PrinterName, DateTimeOffset? StartedAt, double MaterialUsedGrams, int? DurationSeconds,
            Guid? ProjectId, string ProjectName);

        private sealed record PageResult(List<Item> Items, int Page, int PageSize, int TotalCount, int TotalPages);

        private static (PageResult page, string rawJson) ParsePage(CallToolResult result)
        {
            var text = result.Content.OfType<TextContentBlock>().First().Text;
            return (JsonSerializer.Deserialize<PageResult>(text, JsonOptions)!, text);
        }

        private static async Task<CallToolResult> Search(McpClient client, Dictionary<string, object> args) =>
            await client.CallToolAsync(ToolName, args);

        [Fact]
        public async Task Query_FindsPrintByPartialTitle()
        {
            // The single biggest gap before this: with no text search, finding "the soap dish I
            // printed" meant guessing a date window or paginating the entire history.
            await using var client = await _factory.ConnectAsync();
            var (page, _) = ParsePage(await Search(client, new() { ["query"] = "soap dish" }));

            var item = Assert.Single(page.Items);
            Assert.Equal(McpTestData.SoapDishPrintId, item.Id);
        }

        [Fact]
        public async Task Query_MatchesPartialWord_NotJustWholeWords()
        {
            // Substring, not word-boundary: "bottom" and "botto" must both hit.
            await using var client = await _factory.ConnectAsync();
            var (page, _) = ParsePage(await Search(client, new() { ["query"] = "botto" }));

            Assert.Contains(page.Items, i => i.Id == McpTestData.SoapDishPrintId);
        }

        [Fact]
        public async Task Query_IsCaseInsensitive()
        {
            await using var client = await _factory.ConnectAsync();
            var (page, _) = ParsePage(await Search(client, new() { ["query"] = "SOAP DISH" }));

            Assert.Contains(page.Items, i => i.Id == McpTestData.SoapDishPrintId);
        }

        [Fact]
        public async Task Query_MatchesProjectName_AndResultReportsTheProject()
        {
            // A user may remember the project rather than the print. The result must name the
            // project, or the hit is uninterpretable.
            await using var client = await _factory.ConnectAsync();
            var (page, _) = ParsePage(await Search(client, new() { ["query"] = "rocket" }));

            var item = Assert.Single(page.Items);
            Assert.Equal(McpTestData.ProjectPrintId, item.Id);
            Assert.Equal("Rocket Build", item.ProjectName);
            Assert.Equal(McpTestData.ProjectId, item.ProjectId);
        }

        [Fact]
        public async Task ForeignProjectAndPrinter_AreNotLeaked_OnTheCallersOwnPrint()
        {
            // Corrupt cross-owner references on a print the caller DOES own. Gating only on
            // "navigation is not null" would return another user's project and printer names.
            await using var client = await _factory.ConnectAsync();
            var (page, rawJson) = ParsePage(await Search(client, new() { ["pageSize"] = 100 }));

            var item = Assert.Single(page.Items, i => i.Id == McpTestData.CrossOwnerRefPrintId);
            Assert.Null(item.ProjectName);
            Assert.Null(item.ProjectId);
            Assert.Null(item.PrinterName);
            Assert.Null(item.PrinterId);
            Assert.DoesNotContain("SECRET FOREIGN PROJECT", rawJson);
            Assert.DoesNotContain("Other User Printer", rawJson);
        }

        [Fact]
        public async Task Query_DoesNotMatchAForeignProjectName_NoExistenceOracle()
        {
            // Matching on a project the caller does not own would let them confirm another user's
            // project names by guessing them: search, see whether a hit comes back.
            await using var client = await _factory.ConnectAsync();
            var (page, _) = ParsePage(await Search(client, new() { ["query"] = "SECRET FOREIGN" }));

            Assert.Empty(page.Items);
        }

        [Fact]
        public async Task Query_DoesNotSearchNotes()
        {
            // Notes hold pasted slicer dumps; searching them would swamp results with noise.
            // McpTestData seeds "secret notes should never be exposed by search" on Rich Print 1.
            await using var client = await _factory.ConnectAsync();
            var (page, _) = ParsePage(await Search(client, new() { ["query"] = "secret notes" }));

            Assert.Empty(page.Items);
        }

        [Fact]
        public async Task Query_Empty_IsError()
        {
            await using var client = await _factory.ConnectAsync();
            Assert.True(await McpDataWebApplicationFactory.IsToolError(
                client, ToolName, new() { ["query"] = "   " }));
        }

        [Fact]
        public async Task Query_OwnerIsolation_DoesNotFindForeignPrints()
        {
            await using var client = await _factory.ConnectAsync(McpTestData.OtherUserOAuthId);
            var (page, _) = ParsePage(await Search(client, new() { ["query"] = "soap dish" }));

            Assert.Empty(page.Items);
        }

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
