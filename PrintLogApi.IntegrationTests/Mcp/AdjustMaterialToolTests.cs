using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
            await client.CallToolAsync("create_material", new Dictionary<string, object?>
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

            var result = await client.CallToolAsync("adjust_material_remaining", new Dictionary<string, object?>
            {
                ["materialId"] = id,
                ["source"] = "Weight",
                ["delta"] = -200.0,
            });

            Assert.True(result.IsError != true);
            var text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text;
            // Asserted on the parsed fields, not a substring of the raw JSON: "800" would match a
            // material id or any other number that happened to contain it.
            using var doc = JsonDocument.Parse(text);
            Assert.Equal(1000.0, doc.RootElement.GetProperty("beforeGrams").GetDouble());
            Assert.Equal(800.0, doc.RootElement.GetProperty("afterGrams").GetDouble());
        }

        // The reported values are ALWAYS grams, whatever unit the delta was expressed in — which is
        // why the fields are not named *InSourceUnit. 5000 mm of 1.75 mm PLA at 1.24 g/cm3 is ~14.9 g,
        // so a Length delta still reads back on the weight scale.
        [Fact]
        public async Task Adjust_ByLength_StillReportsGrams()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
            var id = await CreateMaterial(client, "Adjust Length Mat");

            var result = await client.CallToolAsync("adjust_material_remaining", new Dictionary<string, object?>
            {
                ["materialId"] = id,
                ["source"] = "Length",
                ["delta"] = -5000.0, // mm
            });

            Assert.True(result.IsError != true);
            var text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text;
            using var doc = JsonDocument.Parse(text);
            Assert.Equal(1000.0, doc.RootElement.GetProperty("beforeGrams").GetDouble());
            // Grams, not the 5000 mm that was passed in.
            Assert.InRange(doc.RootElement.GetProperty("afterGrams").GetDouble(), 984.0, 986.0);
            // The old contract carried a hardcoded "g" SourceUnit that named nothing the caller chose.
            Assert.False(doc.RootElement.TryGetProperty("sourceUnit", out _));
        }

        [Fact]
        public async Task Adjust_BelowZero_IsRejected()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
            var id = await CreateMaterial(client, "Adjust BelowZero Mat");

            var isError = await McpDataWebApplicationFactory.IsToolError(client, "adjust_material_remaining",
                new Dictionary<string, object?> { ["materialId"] = id, ["source"] = "Weight", ["delta"] = -2000.0 });

            Assert.True(isError);
        }

        [Fact]
        public async Task Adjust_AboveCapacity_IsRejected()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
            var id = await CreateMaterial(client, "Adjust AboveCap Mat");

            var isError = await McpDataWebApplicationFactory.IsToolError(client, "adjust_material_remaining",
                new Dictionary<string, object?> { ["materialId"] = id, ["source"] = "Weight", ["delta"] = 500.0 });

            Assert.True(isError);
        }

        [Fact]
        public async Task Adjust_ForeignMaterial_ReturnsNotFound()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            var code = await McpDataWebApplicationFactory.ToolErrorCode(client, "adjust_material_remaining",
                new Dictionary<string, object?>
                {
                    ["materialId"] = Guid.NewGuid(),
                    ["source"] = "Weight",
                    ["delta"] = -1.0,
                });

            Assert.Equal("not_found", code);
        }
    }
}
