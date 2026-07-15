using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    /// <summary>End-to-end tests for the add_material tool.</summary>
    public class AddMaterialToolTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private readonly McpDataWebApplicationFactory _factory;

        public AddMaterialToolTests(McpDataWebApplicationFactory factory) => _factory = factory;

        private static readonly string[] ReadWrite = { "read:printdata", "write:printdata" };

        [Fact]
        public async Task AddMaterial_UnknownCategory_IsRejected()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            var isError = await McpDataWebApplicationFactory.IsToolError(client, "add_material",
                new Dictionary<string, object>
                {
                    ["displayName"] = "Mystery",
                    ["materialType"] = "???",
                    ["materialCategoryNickname"] = "nope",
                    ["densityGramPerCubicCm"] = 1.1,
                    ["source"] = "Weight",
                    ["initialAmount"] = 1000.0,
                });

            Assert.True(isError);
        }

        [Fact]
        public async Task AddMaterial_Resin_ByVolume_Succeeds()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            var result = await client.CallToolAsync("add_material", new Dictionary<string, object>
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
