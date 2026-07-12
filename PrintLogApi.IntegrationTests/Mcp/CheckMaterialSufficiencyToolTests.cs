using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    public class CheckMaterialSufficiencyToolTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private const string ToolName = "check_material_sufficiency";
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly McpDataWebApplicationFactory _factory;

        public CheckMaterialSufficiencyToolTests(McpDataWebApplicationFactory factory) => _factory = factory;

        private sealed record Result(double RequiredGrams, double AvailableGrams, bool Sufficient,
            string Material, string Color);

        private static Result Parse(CallToolResult result)
        {
            var text = result.Content.OfType<TextContentBlock>().First().Text;
            return JsonSerializer.Deserialize<Result>(text, JsonOptions)!;
        }

        private static async Task<Result> Check(McpClient client, Dictionary<string, object> args) =>
            Parse(await client.CallToolAsync(ToolName, args));

        [Fact]
        public async Task ExactEquality_IsSufficient()
        {
            await using var client = await _factory.ConnectAsync();
            // Only active PLA is Filament1 with 975 g remaining.
            var r = await Check(client, new() { ["requiredGrams"] = 975.0, ["material"] = "PLA" });

            Assert.Equal(975.0, r.AvailableGrams);
            Assert.True(r.Sufficient);
        }

        [Fact]
        public async Task JustOver_IsNotSufficient()
        {
            await using var client = await _factory.ConnectAsync();
            var r = await Check(client, new() { ["requiredGrams"] = 976.0, ["material"] = "PLA" });
            Assert.False(r.Sufficient);
        }

        [Fact]
        public async Task EmptyInventory_ReturnsZeroAvailable()
        {
            await using var client = await _factory.ConnectAsync();
            var r = await Check(client, new() { ["requiredGrams"] = 1.0, ["material"] = "NYLON" });
            Assert.Equal(0d, r.AvailableGrams);
            Assert.False(r.Sufficient);
        }

        [Fact]
        public async Task OwnerIsolation_OtherUserHasNothing()
        {
            await using var client = await _factory.ConnectAsync(McpTestData.OtherUserOAuthId);
            var r = await Check(client, new() { ["requiredGrams"] = 1.0 });
            Assert.Equal(0d, r.AvailableGrams);
            Assert.False(r.Sufficient);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-5.0)]
        public async Task NonPositiveRequired_IsError(double required)
        {
            await using var client = await _factory.ConnectAsync();
            Assert.True(await McpDataWebApplicationFactory.IsToolError(
                client, ToolName, new() { ["requiredGrams"] = required }));
        }
    }
}
