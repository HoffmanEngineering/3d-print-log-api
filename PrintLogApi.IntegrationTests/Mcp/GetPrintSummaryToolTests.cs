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
    public class GetPrintSummaryToolTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private const string ToolName = "get_print_summary";
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly McpDataWebApplicationFactory _factory;

        public GetPrintSummaryToolTests(McpDataWebApplicationFactory factory) => _factory = factory;

        private sealed record Metrics(int Prints, double MaterialUsedGrams, int TotalPrintTimeSeconds);

        private sealed record Summary(DateTimeOffset? From, DateTimeOffset? To,
            string AppliedStatusFilter, Metrics Filtered,
            Dictionary<string, int> UnfilteredStatusCounts, Metrics Undated);

        private static Summary Parse(CallToolResult result)
        {
            var text = result.Content.OfType<TextContentBlock>().First().Text;
            return JsonSerializer.Deserialize<Summary>(text, JsonOptions)!;
        }

        private static async Task<Summary> Get(McpClient client, Dictionary<string, object> args) =>
            Parse(await client.CallToolAsync(ToolName, args));

        private static readonly DateTimeOffset FullFrom = McpTestData.RichPrint2Date.AddDays(-30);
        private static readonly DateTimeOffset FullTo = McpTestData.RichPrint2Date.AddDays(1);

        [Fact]
        public async Task Summary_AggregatesCountsMaterialAndTime()
        {
            await using var client = await _factory.ConnectAsync();
            var s = await Get(client, new() { ["from"] = FullFrom, ["to"] = FullTo });

            Assert.Equal(7, s.Filtered.Prints);                  // 5 base + 2 rich
            Assert.Equal(35.0, s.Filtered.MaterialUsedGrams);    // 25 g + 10 g
            Assert.Equal(10800, s.Filtered.TotalPrintTimeSeconds); // 7200 + 3600
        }

        [Fact]
        public async Task StatusCounts_CoverAllStatuses_IncludingZeros()
        {
            // An absent key would read as "unknown", not "none". The old contract exposed only
            // successful/failed and had nowhere to put the other four statuses.
            await using var client = await _factory.ConnectAsync();
            var s = await Get(client, new() { ["from"] = FullFrom, ["to"] = FullTo });

            Assert.Equal(6, s.UnfilteredStatusCounts.Count);
            Assert.Equal(3, s.UnfilteredStatusCounts["Success"]);
            Assert.Equal(1, s.UnfilteredStatusCounts["Failed"]);
            Assert.Equal(0, s.UnfilteredStatusCounts["Cancelled"]);
            Assert.Equal(0, s.UnfilteredStatusCounts["PartialSuccess"]);
        }

        [Fact]
        public async Task StatusFilter_ScopesTheMetrics_ButNotTheBreakdown()
        {
            // "How many did I finish, and what's the breakdown?" needs BOTH populations, and they
            // must be structurally distinguishable so an agent cannot compare them by mistake.
            await using var client = await _factory.ConnectAsync();
            var s = await Get(client, new() { ["from"] = FullFrom, ["to"] = FullTo, ["status"] = "Success" });

            Assert.Equal("Success", s.AppliedStatusFilter);
            Assert.Equal(3, s.Filtered.Prints);
            Assert.Equal(s.UnfilteredStatusCounts["Success"], s.Filtered.Prints);

            // The breakdown is NOT filtered — the failed print is still counted there.
            Assert.Equal(1, s.UnfilteredStatusCounts["Failed"]);
        }

        [Fact]
        public async Task AllTime_IncludesUndatedPrints_AndReconcilesExactly()
        {
            // Undated prints can never appear in a date range, so all-time != sum(ranges) unless the
            // undated block is reported. A bare count would not explain the grams/seconds gap either.
            await using var client = await _factory.ConnectAsync();

            var all = await Get(client, new() { });
            var ranged = await Get(client, new() { ["from"] = FullFrom, ["to"] = FullTo });

            Assert.Null(all.From);
            Assert.True(all.Undated.Prints > 0);

            Assert.Equal(ranged.Filtered.Prints + all.Undated.Prints, all.Filtered.Prints);
            Assert.Equal(
                ranged.Filtered.MaterialUsedGrams + all.Undated.MaterialUsedGrams,
                all.Filtered.MaterialUsedGrams, 3);
            Assert.Equal(
                ranged.Filtered.TotalPrintTimeSeconds + all.Undated.TotalPrintTimeSeconds,
                all.Filtered.TotalPrintTimeSeconds);
        }

        [Fact]
        public async Task RangedQuery_ReportsZeroUndated_SoTheShapeNeverChanges()
        {
            await using var client = await _factory.ConnectAsync();
            var s = await Get(client, new() { ["from"] = FullFrom, ["to"] = FullTo });

            Assert.Equal(0, s.Undated.Prints);
            Assert.Equal(0d, s.Undated.MaterialUsedGrams);
        }

        [Fact]
        public async Task NarrowRange_CountsOnlyInRange()
        {
            await using var client = await _factory.ConnectAsync();
            var s = await Get(client, new()
            {
                ["from"] = McpTestData.RichPrint1Date.AddHours(-1),
                ["to"] = McpTestData.RichPrint2Date.AddHours(1),
            });

            Assert.Equal(2, s.Filtered.Prints);
            Assert.Equal(35.0, s.Filtered.MaterialUsedGrams);
        }

        [Fact]
        public async Task EmptyRange_IsAllZeros()
        {
            await using var client = await _factory.ConnectAsync();
            var s = await Get(client, new()
            {
                ["from"] = FullFrom.AddYears(-5),
                ["to"] = FullFrom.AddYears(-5).AddDays(1),
            });

            Assert.Equal(0, s.Filtered.Prints);
            Assert.Equal(0d, s.Filtered.MaterialUsedGrams);
            Assert.Equal(0, s.UnfilteredStatusCounts["Success"]);
        }

        [Fact]
        public async Task OwnerIsolation_OtherUserSeesOnlyOwnPrints()
        {
            await using var client = await _factory.ConnectAsync(McpTestData.OtherUserOAuthId);
            var s = await Get(client, new() { ["from"] = FullFrom, ["to"] = FullTo });

            Assert.Equal(1, s.Filtered.Prints); // only the foreign print
            Assert.Equal(99.0, s.Filtered.MaterialUsedGrams);
        }

        [Fact]
        public async Task OverlongRange_IsError()
        {
            await using var client = await _factory.ConnectAsync();
            Assert.True(await McpDataWebApplicationFactory.IsToolError(client, ToolName,
                new() { ["from"] = FullFrom, ["to"] = FullFrom.AddDays(367) }));
        }

        [Fact]
        public async Task HalfOpenRange_IsError()
        {
            // Supplying only one endpoint must not be silently treated as all-time — that would
            // answer a different question than the caller asked.
            await using var client = await _factory.ConnectAsync();
            Assert.True(await McpDataWebApplicationFactory.IsToolError(
                client, ToolName, new() { ["from"] = FullFrom }));
        }
    }
}
