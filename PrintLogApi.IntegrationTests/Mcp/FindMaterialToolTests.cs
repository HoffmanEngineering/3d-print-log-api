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
    /// <summary>
    /// find_material replaces check_material_sufficiency, which answered "do I have enough X?" with
    /// a single boolean over a mixture of incompatible materials and colours. These tests pin the
    /// behaviour that made the replacement necessary.
    /// </summary>
    public class FindMaterialToolTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private const string ToolName = "find_material";
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly McpDataWebApplicationFactory _factory;

        public FindMaterialToolTests(McpDataWebApplicationFactory factory) => _factory = factory;

        private sealed record Spool(Guid Id, string Name, string Brand, string Material,
            string Color, double? DiameterMm, double RemainingGrams, string StorageLocation);

        private sealed record Group(string Material, string Color, int SpoolCount,
            double TotalGrams, double LargestSpoolGrams, List<Spool> Spools, bool SpoolsTruncated,
            bool? SufficientOnLargestSpool, bool? MeetsRequirementByCombiningSpools,
            List<Spool> CombinationForRequirement);

        private sealed record Result(double? RequiredGrams, List<Group> Groups,
            bool GroupsTruncated, bool CandidatesTruncated);

        private static Result Parse(CallToolResult result)
        {
            var text = result.Content.OfType<TextContentBlock>().First().Text;
            return JsonSerializer.Deserialize<Result>(text, JsonOptions)!;
        }

        private static async Task<Result> Get(McpClient client, Dictionary<string, object> args) =>
            Parse(await client.CallToolAsync(ToolName, args));

        [Fact]
        public async Task Groups_NeverMergeDifferentMaterials()
        {
            // Crimson inventory: PLA 250 g + 150 g + (-200 g corrupt), and PLA-CF 500 g.
            // The old tool summed all of these and would call a 600 g plain-PLA print printable.
            await using var client = await _factory.ConnectAsync();
            var result = await Get(client, new() { ["material"] = "PLA", ["color"] = "crimson", ["requiredGrams"] = 600.0 });

            var pla = result.Groups.Single(g => g.Material == "PLA" && g.Color == "Crimson");
            var carbon = result.Groups.Single(g => g.Material == "PLA-CF" && g.Color == "Crimson");

            Assert.False(pla.MeetsRequirementByCombiningSpools);
            Assert.False(carbon.MeetsRequirementByCombiningSpools);
            Assert.DoesNotContain(result.Groups, g => g.TotalGrams >= 600);
        }

        [Fact]
        public async Task NegativeRemaining_ClampsToZero_AndDoesNotReduceOtherSpools()
        {
            // PLA/Crimson = 250 + 150 + (-200). Clamped: 400 g, not 200 g. Without the clamp a single
            // corrupt spool would make a printable 300 g job look unprintable.
            await using var client = await _factory.ConnectAsync();
            var result = await Get(client, new() { ["material"] = "PLA", ["color"] = "crimson", ["requiredGrams"] = 300.0 });

            var pla = result.Groups.Single(g => g.Material == "PLA" && g.Color == "Crimson");

            Assert.Equal(400d, pla.TotalGrams);
            Assert.True(pla.MeetsRequirementByCombiningSpools);
            Assert.False(pla.SufficientOnLargestSpool); // largest single spool is only 250 g
        }

        [Fact]
        public async Task Combination_ReturnsMinimalPrefix_LargestFirst()
        {
            // The user asks "I have a 300 g model, do I have crimson PLA?" The agent must be able to say
            // "250 g from one spool and 50 g from another".
            await using var client = await _factory.ConnectAsync();
            var result = await Get(client, new() { ["material"] = "PLA", ["color"] = "crimson", ["requiredGrams"] = 300.0 });

            var pla = result.Groups.Single(g => g.Material == "PLA" && g.Color == "Crimson");

            Assert.Equal(2, pla.CombinationForRequirement.Count);
            Assert.Equal(250d, pla.CombinationForRequirement[0].RemainingGrams);
            Assert.Equal(150d, pla.CombinationForRequirement[1].RemainingGrams);
        }

        [Fact]
        public async Task SufficientOnLargestSpool_WhenOneSpoolIsEnough()
        {
            await using var client = await _factory.ConnectAsync();
            var result = await Get(client, new() { ["material"] = "PLA", ["color"] = "crimson", ["requiredGrams"] = 200.0 });

            var pla = result.Groups.Single(g => g.Material == "PLA" && g.Color == "Crimson");

            Assert.True(pla.SufficientOnLargestSpool);
            Assert.Single(pla.CombinationForRequirement); // the 250 g spool alone
        }

        [Fact]
        public async Task GroupOrdering_SingleSpoolSolutionRanksFirst()
        {
            // Ordering by total grams alone would rank the PLA group (400 g total) above the PLA-CF
            // group (500 g on ONE spool) — hiding the only unattended solution behind a cap.
            await using var client = await _factory.ConnectAsync();
            var result = await Get(client, new() { ["color"] = "crimson", ["requiredGrams"] = 450.0 });

            Assert.True(result.Groups[0].SufficientOnLargestSpool);
            Assert.Equal("PLA-CF", result.Groups[0].Material);
        }

        [Fact]
        public async Task NoRequiredGrams_OmitsSufficiencyFields()
        {
            await using var client = await _factory.ConnectAsync();
            var result = await Get(client, new() { ["material"] = "PLA", ["color"] = "crimson" });

            var pla = result.Groups.Single(g => g.Material == "PLA" && g.Color == "Crimson");

            Assert.Null(pla.SufficientOnLargestSpool);
            Assert.Null(pla.MeetsRequirementByCombiningSpools);
            Assert.Null(pla.CombinationForRequirement);
        }

        [Fact]
        public async Task OwnerIsolation_OtherUserSeesNothing()
        {
            await using var client = await _factory.ConnectAsync(McpTestData.OtherUserOAuthId);
            var result = await Get(client, new() { ["material"] = "PLA" });

            Assert.Empty(result.Groups);
        }

        [Fact]
        public async Task RequiredGrams_MustBePositive()
        {
            await using var client = await _factory.ConnectAsync();

            Assert.True(await McpDataWebApplicationFactory.IsToolError(
                client, ToolName, new() { ["requiredGrams"] = 0.0 }));
            Assert.True(await McpDataWebApplicationFactory.IsToolError(
                client, ToolName, new() { ["requiredGrams"] = -5.0 }));
        }
    }
}
