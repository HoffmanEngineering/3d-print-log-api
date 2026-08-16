using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    /// <summary>
    /// End-to-end tests for the create_material tool. Field-level behavior is covered by
    /// CreateMaterialServiceTests; these pin what survives the wire.
    /// </summary>
    public class CreateMaterialToolTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private readonly McpDataWebApplicationFactory _factory;

        public CreateMaterialToolTests(McpDataWebApplicationFactory factory) => _factory = factory;

        private static readonly string[] ReadWrite = { "read:printdata", "write:printdata" };

        [Fact]
        public async Task CreateMaterial_UnknownCategory_IsRejected()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            var result = await client.CallToolAsync("create_material", new Dictionary<string, object?>
            {
                ["displayName"] = "Mystery",
                ["materialType"] = "???",
                ["materialCategoryNickname"] = "nope",
                ["densityGramPerCubicCm"] = 1.1,
                ["source"] = "Weight",
                ["initialAmount"] = 1000.0,
            });

            Assert.True(result.IsError == true);
            // Assert the REASON, not just that something failed: a bare IsError check also passes when
            // the tool does not exist at all, which would hide a rename or a registration break.
            var text = result.Content.OfType<TextContentBlock>().First().Text;
            Assert.StartsWith("invalid_arguments:", text);
            Assert.Contains("nope", text);
        }

        [Fact]
        public async Task CreateMaterial_Resin_ByVolume_Succeeds()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            var result = await client.CallToolAsync("create_material", new Dictionary<string, object?>
            {
                ["displayName"] = "Test Grey Resin",
                ["materialType"] = "Resin",
                ["materialCategoryNickname"] = "resin", // HasDiameter = false, so no diameter needed
                ["densityGramPerCubicCm"] = 1.1,
                ["source"] = "Volume",
                ["initialAmount"] = 1000.0, // 1000 ml
            });

            Assert.True(result.IsError != true);
            var text = result.Content.OfType<TextContentBlock>().First().Text;
            Assert.Contains("Test Grey Resin", text);
        }
    }
}
