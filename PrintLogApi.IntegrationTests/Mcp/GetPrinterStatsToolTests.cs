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
    public class GetPrinterStatsToolTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private const string ToolName = "get_printer_stats";
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly McpDataWebApplicationFactory _factory;

        public GetPrinterStatsToolTests(McpDataWebApplicationFactory factory) => _factory = factory;

        private sealed record Stat(long PrinterId, string PrinterName, int TotalPrints,
            int SuccessfulPrints, int FailedPrints, double SuccessRatePercent, int TotalPrintTimeSeconds);

        private static List<Stat> Parse(CallToolResult result)
        {
            var text = result.Content.OfType<TextContentBlock>().First().Text;
            return JsonSerializer.Deserialize<List<Stat>>(text, JsonOptions)!;
        }

        private static readonly DateTimeOffset FullFrom = McpTestData.RichPrint2Date.AddDays(-30);
        private static readonly DateTimeOffset FullTo = McpTestData.RichPrint2Date.AddDays(1);

        [Fact]
        public async Task Stats_CountStatusesDurationAndRate_OrderedByName()
        {
            await using var client = await _factory.ConnectAsync();
            var stats = Parse(await client.CallToolAsync(ToolName,
                new Dictionary<string, object> { ["from"] = FullFrom, ["to"] = FullTo }));

            Assert.Equal(2, stats.Count);
            Assert.Equal(new[] { IntegrationTestSeeder.TestPrinterId, IntegrationTestSeeder.TestPrinterId2 },
                stats.Select(s => s.PrinterId).ToArray());

            var printer1 = stats.Single(s => s.PrinterId == IntegrationTestSeeder.TestPrinterId);
            Assert.Equal(5, printer1.TotalPrints);
            Assert.Equal(2, printer1.SuccessfulPrints);
            Assert.Equal(0, printer1.FailedPrints);
            Assert.Equal(40.0, printer1.SuccessRatePercent);
            Assert.Equal(0, printer1.TotalPrintTimeSeconds);

            var printer2 = stats.Single(s => s.PrinterId == IntegrationTestSeeder.TestPrinterId2);
            Assert.Equal(2, printer2.TotalPrints);
            Assert.Equal(1, printer2.SuccessfulPrints);
            Assert.Equal(1, printer2.FailedPrints);
            Assert.Equal(50.0, printer2.SuccessRatePercent);
            Assert.Equal(10800, printer2.TotalPrintTimeSeconds);
        }

        [Fact]
        public async Task DateRange_ExcludesOutOfRangePrints()
        {
            await using var client = await _factory.ConnectAsync();
            // Narrow window covering only the two rich prints (both on Printer 2).
            var stats = Parse(await client.CallToolAsync(ToolName, new Dictionary<string, object>
            {
                ["from"] = McpTestData.RichPrint1Date.AddHours(-1),
                ["to"] = McpTestData.RichPrint2Date.AddHours(1),
            }));

            Assert.Single(stats);
            Assert.Equal(IntegrationTestSeeder.TestPrinterId2, stats[0].PrinterId);
            Assert.Equal(2, stats[0].TotalPrints);
        }

        [Fact]
        public async Task OwnerIsolation_OtherUserSeesOnlyOwnPrinter()
        {
            await using var client = await _factory.ConnectAsync(McpTestData.OtherUserOAuthId);
            var stats = Parse(await client.CallToolAsync(ToolName,
                new Dictionary<string, object> { ["from"] = FullFrom, ["to"] = FullTo }));

            Assert.Single(stats);
            Assert.Equal(McpTestData.OtherPrinterId, stats[0].PrinterId);
        }

        [Fact]
        public async Task OverlongRange_IsError()
        {
            await using var client = await _factory.ConnectAsync();
            Assert.True(await McpDataWebApplicationFactory.IsToolError(client, ToolName,
                new() { ["from"] = FullFrom, ["to"] = FullFrom.AddDays(367) }));
        }

        [Fact]
        public async Task Exactly366Days_IsAccepted()
        {
            await using var client = await _factory.ConnectAsync();
            Assert.False(await McpDataWebApplicationFactory.IsToolError(client, ToolName,
                new() { ["from"] = FullFrom, ["to"] = FullFrom.AddDays(366) }));
        }
    }
}
