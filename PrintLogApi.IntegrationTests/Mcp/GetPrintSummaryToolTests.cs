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

        private sealed record Summary(DateTimeOffset From, DateTimeOffset To, int TotalPrints,
            int SuccessfulPrints, int FailedPrints, double MaterialUsedGrams, int TotalPrintTimeSeconds);

        private static Summary Parse(CallToolResult result)
        {
            var text = result.Content.OfType<TextContentBlock>().First().Text;
            return JsonSerializer.Deserialize<Summary>(text, JsonOptions)!;
        }

        private static readonly DateTimeOffset FullFrom = McpTestData.RichPrint2Date.AddDays(-30);
        private static readonly DateTimeOffset FullTo = McpTestData.RichPrint2Date.AddDays(1);

        [Fact]
        public async Task Summary_AggregatesCountsMaterialAndTime()
        {
            await using var client = await _factory.ConnectAsync();
            var s = Parse(await client.CallToolAsync(ToolName,
                new Dictionary<string, object> { ["from"] = FullFrom, ["to"] = FullTo }));

            Assert.Equal(7, s.TotalPrints);   // 5 base + 2 rich
            Assert.Equal(3, s.SuccessfulPrints); // 2 base + rich1
            Assert.Equal(1, s.FailedPrints);  // rich2
            Assert.Equal(35.0, s.MaterialUsedGrams); // 25 g + 10 g
            Assert.Equal(10800, s.TotalPrintTimeSeconds); // 7200 + 3600
        }

        [Fact]
        public async Task NarrowRange_CountsOnlyInRange()
        {
            await using var client = await _factory.ConnectAsync();
            var s = Parse(await client.CallToolAsync(ToolName, new Dictionary<string, object>
            {
                ["from"] = McpTestData.RichPrint1Date.AddHours(-1),
                ["to"] = McpTestData.RichPrint2Date.AddHours(1),
            }));

            Assert.Equal(2, s.TotalPrints);
            Assert.Equal(1, s.SuccessfulPrints);
            Assert.Equal(1, s.FailedPrints);
            Assert.Equal(35.0, s.MaterialUsedGrams);
        }

        [Fact]
        public async Task EmptyRange_IsAllZeros()
        {
            await using var client = await _factory.ConnectAsync();
            var s = Parse(await client.CallToolAsync(ToolName, new Dictionary<string, object>
            {
                ["from"] = FullFrom.AddYears(-5),
                ["to"] = FullFrom.AddYears(-5).AddDays(1),
            }));

            Assert.Equal(0, s.TotalPrints);
            Assert.Equal(0, s.SuccessfulPrints);
            Assert.Equal(0d, s.MaterialUsedGrams);
        }

        [Fact]
        public async Task OwnerIsolation_OtherUserSeesOnlyOwnPrints()
        {
            await using var client = await _factory.ConnectAsync(McpTestData.OtherUserOAuthId);
            var s = Parse(await client.CallToolAsync(ToolName,
                new Dictionary<string, object> { ["from"] = FullFrom, ["to"] = FullTo }));

            Assert.Equal(1, s.TotalPrints); // only the foreign print
            Assert.Equal(1, s.SuccessfulPrints);
            Assert.Equal(99.0, s.MaterialUsedGrams);
        }

        [Fact]
        public async Task OverlongRange_IsError()
        {
            await using var client = await _factory.ConnectAsync();
            Assert.True(await McpDataWebApplicationFactory.IsToolError(client, ToolName,
                new() { ["from"] = FullFrom, ["to"] = FullFrom.AddDays(367) }));
        }
    }
}
