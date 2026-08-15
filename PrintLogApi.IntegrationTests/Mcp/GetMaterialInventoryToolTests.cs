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
            string Color, double RemainingGrams, bool IsActive,
            string StorageLocation, double? DiameterMm);

        private sealed record PageResult(List<Item> Items, int Page, int PageSize, int TotalCount, int TotalPages);

        private static PageResult Parse(CallToolResult result)
        {
            var text = result.Content.OfType<TextContentBlock>().First().Text;
            return JsonSerializer.Deserialize<PageResult>(text, JsonOptions)!;
        }

        private static async Task<PageResult> Get(McpClient client, Dictionary<string, object?> args) =>
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
            // 3 base seeds + 5 text-matching fixtures + 4 find_material fixtures
            // + 12 AMS spools (Nylon/Amber, unique so they match no other test's filter)
            // + 1 resin (no diameter, used by the usage-convertibility tests).
            Assert.Equal(25, page.Items.Count);
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
        public async Task FilterByMaterial_MatchesWholeWords_NotExactStrings()
        {
            // This previously asserted EXACT matching and returned only TestFilamentId1 — which was
            // the bug: real users store the same material as "PLA", "PLA (Polylactic Acid)" and
            // "PLA+", and exact matching found only the first.
            await using var client = await _factory.ConnectAsync();
            var page = await Get(client, new() { ["material"] = "pla", ["pageSize"] = 100 });

            var materials = page.Items.Select(i => i.Material).ToList();
            Assert.Contains("PLA", materials);
            Assert.Contains("PLA (Polylactic Acid)", materials);
            Assert.Contains("PLA+", materials);

            // ...but must not become a substring match.
            Assert.DoesNotContain(materials, m => m.StartsWith("PETG"));
            Assert.DoesNotContain("PCTG", materials);
        }

        [Fact]
        public async Task FilterByMaterial_ShortAcronym_DoesNotSubstringMatch()
        {
            await using var client = await _factory.ConnectAsync();
            var page = await Get(client, new() { ["material"] = "PC", ["pageSize"] = 100 });

            Assert.DoesNotContain(page.Items, i => i.Material == "PCTG");
        }

        [Fact]
        public async Task FilterByColor_MatchesQualifiedColors()
        {
            // "blue" must find "Light Blue". In production this doubles the hit count for blue.
            await using var client = await _factory.ConnectAsync();
            var page = await Get(client, new() { ["color"] = "blue", ["pageSize"] = 100 });

            var colors = page.Items.Select(i => i.Color).ToList();
            Assert.Contains("Blue", colors);
            Assert.Contains("Light Blue", colors);
            Assert.DoesNotContain("Navy", colors);
        }

        [Fact]
        public async Task Items_ExposeStorageLocationAndDiameter()
        {
            await using var client = await _factory.ConnectAsync();
            var page = await Get(client, new() { ["pageSize"] = 100 });

            var fixture = page.Items.First(i => i.Material == "PLA+");
            Assert.Equal(1.75, fixture.DiameterMm);
        }

        [Theory]
        [InlineData("+++")]
        [InlineData("---")]
        [InlineData("()")]
        public async Task PunctuationOnlyFilter_IsRejected_NotTreatedAsNoFilter(string material)
        {
            // Such a filter normalizes to empty and would otherwise match EVERY row.
            await using var client = await _factory.ConnectAsync();
            Assert.True(await McpDataWebApplicationFactory.IsToolError(
                client, ToolName, new() { ["material"] = material }));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task ExplicitlyEmptyFilter_IsRejected_NotTreatedAsNoFilter(string material)
        {
            // An explicitly supplied empty filter must NOT silently degrade to "no filter": that
            // hands back the entire inventory to a caller who believes it filtered.
            await using var client = await _factory.ConnectAsync();
            Assert.True(await McpDataWebApplicationFactory.IsToolError(
                client, ToolName, new() { ["material"] = material }));
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
