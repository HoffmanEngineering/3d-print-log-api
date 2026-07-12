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
    public class GetMaterialInventoryToolTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private const string ToolName = "get_material_inventory";
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly McpDataWebApplicationFactory _factory;

        public GetMaterialInventoryToolTests(McpDataWebApplicationFactory factory) => _factory = factory;

        private sealed record Item(Guid Id, string Name, string Brand, string Material,
            string Color, double RemainingGrams, bool IsActive);

        private sealed record PageResult(List<Item> Items, int Page, int PageSize, int TotalCount, int TotalPages);

        private static PageResult Parse(CallToolResult result)
        {
            var text = result.Content.OfType<TextContentBlock>().First().Text;
            return JsonSerializer.Deserialize<PageResult>(text, JsonOptions)!;
        }

        private static async Task<PageResult> Get(McpClient client, Dictionary<string, object> args) =>
            Parse(await client.CallToolAsync(ToolName, args));

        [Fact]
        public async Task OwnerIsolation_OtherUserSeesNothing()
        {
            await using var client = await _factory.ConnectAsync(McpTestData.OtherUserOAuthId);
            var page = await Get(client, new() { ["pageSize"] = 100 });
            Assert.Empty(page.Items);
        }

        [Fact]
        public async Task Default_ExcludesInactive()
        {
            await using var client = await _factory.ConnectAsync();
            var page = await Get(client, new() { ["pageSize"] = 100 });

            Assert.DoesNotContain(page.Items, i => i.Id == McpTestData.InactiveFilamentId);
            Assert.Equal(3, page.Items.Count);
        }

        [Fact]
        public async Task IncludeInactive_AddsInactiveWithZeroRemaining()
        {
            await using var client = await _factory.ConnectAsync();
            var page = await Get(client, new() { ["includeInactive"] = true, ["pageSize"] = 100 });

            var inactive = page.Items.Single(i => i.Id == McpTestData.InactiveFilamentId);
            Assert.False(inactive.IsActive);
            Assert.Equal(0d, inactive.RemainingGrams); // null initial weight => 0 grams
        }

        [Fact]
        public async Task Remaining_IsGrams_NetOfUsage()
        {
            await using var client = await _factory.ConnectAsync();
            var page = await Get(client, new() { ["pageSize"] = 100 });

            // Filament1: 1,000,000 mg initial - 25,000 mg used = 975 g.
            var f1 = page.Items.Single(i => i.Id == IntegrationTestSeeder.TestFilamentId1);
            Assert.Equal(975.0, f1.RemainingGrams);
        }

        [Fact]
        public async Task FilterByMaterial_IsCaseInsensitiveExact()
        {
            await using var client = await _factory.ConnectAsync();
            var page = await Get(client, new() { ["material"] = "pla", ["pageSize"] = 100 });

            Assert.Equal(new[] { IntegrationTestSeeder.TestFilamentId1 }, page.Items.Select(i => i.Id).ToArray());
        }

        [Fact]
        public async Task PageSize_ClampsTo100()
        {
            await using var client = await _factory.ConnectAsync();
            var page = await Get(client, new() { ["pageSize"] = 1000 });
            Assert.Equal(100, page.PageSize);
        }

        [Fact]
        public async Task Page0_IsError()
        {
            await using var client = await _factory.ConnectAsync();
            Assert.True(await McpDataWebApplicationFactory.IsToolError(client, ToolName, new() { ["page"] = 0 }));
        }
    }
}
