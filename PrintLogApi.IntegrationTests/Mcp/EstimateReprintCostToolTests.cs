using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    public class EstimateReprintCostToolTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private const string ToolName = "estimate_reprint_cost";
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly McpDataWebApplicationFactory _factory;

        public EstimateReprintCostToolTests(McpDataWebApplicationFactory factory) => _factory = factory;

        private sealed record Cost(long PrintId, decimal? EstimatedCost, string Currency,
            double MaterialGrams, int? DurationSeconds);

        private static Cost Parse(CallToolResult result)
        {
            var text = result.Content.OfType<TextContentBlock>().First().Text;
            return JsonSerializer.Deserialize<Cost>(text, JsonOptions)!;
        }

        [Fact]
        public async Task Creator_GetsMaterialDurationAndCurrency_NullCost()
        {
            await using var client = await _factory.ConnectAsync();
            var cost = Parse(await client.CallToolAsync(ToolName,
                new Dictionary<string, object> { ["printId"] = McpTestData.RichPrintId1 }));

            Assert.Equal(McpTestData.RichPrintId1, cost.PrintId);
            Assert.Null(cost.EstimatedCost); // v1: no trustworthy server-side cost calculation
            Assert.Equal(McpTestData.PrimaryUserCurrency, cost.Currency);
            Assert.Equal(25.0, cost.MaterialGrams);
            Assert.Equal(7200, cost.DurationSeconds);
        }

        [Fact]
        public async Task ForeignPublicPrint_IsError()
        {
            await using var client = await _factory.ConnectAsync();
            Assert.True(await McpDataWebApplicationFactory.IsToolError(
                client, ToolName, new() { ["printId"] = McpTestData.ForeignPrintId }));
        }

        [Fact]
        public async Task MissingPrint_IsError()
        {
            await using var client = await _factory.ConnectAsync();
            Assert.True(await McpDataWebApplicationFactory.IsToolError(
                client, ToolName, new() { ["printId"] = 999999L }));
        }

        [Fact]
        public async Task NoCurrencySetting_DefaultsToUsd()
        {
            // The other user has no currency setting; ensure a sensible default and creator scope.
            // Seed a print for the other user is unnecessary: verify default via their own printer print.
            await using var client = await _factory.ConnectAsync(McpTestData.OtherUserOAuthId);
            var cost = Parse(await client.CallToolAsync(ToolName,
                new Dictionary<string, object> { ["printId"] = McpTestData.ForeignPrintId }));

            Assert.Equal("USD", cost.Currency);
            Assert.Equal(99.0, cost.MaterialGrams);
        }
    }
}
