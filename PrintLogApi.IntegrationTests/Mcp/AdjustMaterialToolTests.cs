using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    /// <summary>End-to-end tests for adjust_material_remaining, including the capacity bounds.</summary>
    public class AdjustMaterialToolTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private readonly McpDataWebApplicationFactory _factory;

        public AdjustMaterialToolTests(McpDataWebApplicationFactory factory) => _factory = factory;

        private static readonly string[] ReadWrite = { "read:printdata", "write:printdata" };

        // Creates a 1000 g weight-measured material and returns its id, so each test mutates its own
        // material rather than a shared read-fixture.
        private async Task<Guid> CreateMaterial(McpClient client, string name)
        {
            await client.CallToolAsync("create_material", new Dictionary<string, object>
            {
                ["displayName"] = name,
                ["materialType"] = "PLA",
                ["materialCategoryNickname"] = "filament",
                ["densityGramPerCubicCm"] = 1.24,
                ["diameterMm"] = 1.75,
                ["source"] = "Weight",
                ["initialAmount"] = 1000.0, // 1000 g
            });

            using var scope = _factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            return ctx.Filaments.Where(f => f.DisplayName == name).Select(f => f.Id).First();
        }

        [Fact]
        public async Task Adjust_Down_ReturnsBeforeAndAfter()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
            var id = await CreateMaterial(client, "Adjust Down Mat");

            var result = await client.CallToolAsync("adjust_material_remaining", new Dictionary<string, object>
            {
                ["materialId"] = id,
                ["source"] = "Weight",
                ["delta"] = -200.0,
            });

            Assert.True(result.IsError != true);
            var text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text;
            Assert.Contains("800", text); // after = 1000 - 200
        }

        [Fact]
        public async Task Adjust_BelowZero_IsRejected()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
            var id = await CreateMaterial(client, "Adjust BelowZero Mat");

            var isError = await McpDataWebApplicationFactory.IsToolError(client, "adjust_material_remaining",
                new Dictionary<string, object> { ["materialId"] = id, ["source"] = "Weight", ["delta"] = -2000.0 });

            Assert.True(isError);
        }

        [Fact]
        public async Task Adjust_AboveCapacity_IsRejected()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
            var id = await CreateMaterial(client, "Adjust AboveCap Mat");

            var isError = await McpDataWebApplicationFactory.IsToolError(client, "adjust_material_remaining",
                new Dictionary<string, object> { ["materialId"] = id, ["source"] = "Weight", ["delta"] = 500.0 });

            Assert.True(isError);
        }

        [Fact]
        public async Task Adjust_ForeignMaterial_ReturnsNotFound()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            var code = await McpDataWebApplicationFactory.ToolErrorCode(client, "adjust_material_remaining",
                new Dictionary<string, object>
                {
                    ["materialId"] = Guid.NewGuid(),
                    ["source"] = "Weight",
                    ["delta"] = -1.0,
                });

            Assert.Equal("not_found", code);
        }
    }
}
