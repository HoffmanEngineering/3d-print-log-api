using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    /// <summary>End-to-end tests for set_material_active.</summary>
    public class SetMaterialActiveToolTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private readonly McpDataWebApplicationFactory _factory;

        public SetMaterialActiveToolTests(McpDataWebApplicationFactory factory) => _factory = factory;

        private static readonly string[] ReadWrite = { "read:printdata", "write:printdata" };

        [Fact]
        public async Task SetMaterialActive_TogglesFlag()
        {
            // InactiveFilamentId is the primary user's, currently inactive. Activate it.
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            var result = await client.CallToolAsync("set_material_active", new Dictionary<string, object?>
            {
                ["materialId"] = McpTestData.InactiveFilamentId,
                ["isActive"] = true,
            });
            Assert.True(result.IsError != true);

            using var scope = _factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var isActive = ctx.Filaments.Where(f => f.Id == McpTestData.InactiveFilamentId).Select(f => f.IsActive).First();
            Assert.True(isActive);
        }

        [Fact]
        public async Task SetMaterialActive_ForeignMaterial_ReturnsNotFound()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            var code = await McpDataWebApplicationFactory.ToolErrorCode(client, "set_material_active",
                new Dictionary<string, object?> { ["materialId"] = Guid.NewGuid(), ["isActive"] = false });

            Assert.Equal("not_found", code);
        }
    }
}
