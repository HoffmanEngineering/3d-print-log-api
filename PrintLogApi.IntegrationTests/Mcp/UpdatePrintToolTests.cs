using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    /// <summary>End-to-end tests for the update_print tool: ownership and patch semantics.</summary>
    public class UpdatePrintToolTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private readonly McpDataWebApplicationFactory _factory;

        public UpdatePrintToolTests(McpDataWebApplicationFactory factory) => _factory = factory;

        private static readonly string[] ReadWrite = { "read:printdata", "write:printdata" };

        [Fact]
        public async Task UpdatePrint_ByNonCreator_ReturnsNotFound()
        {
            // RichPrintId1 belongs to the primary user; the other user must not be able to edit it.
            await using var client = await _factory.ConnectAsync(McpTestData.OtherUserOAuthId, ReadWrite);

            var code = await McpDataWebApplicationFactory.ToolErrorCode(client, "update_print",
                new Dictionary<string, object> { ["id"] = McpTestData.RichPrintId1, ["status"] = "Failed" });

            Assert.Equal("not_found", code);
        }

        [Fact]
        public async Task UpdatePrint_OmittedProject_Leaves_ExplicitClear_Removes()
        {
            // ProjectPrintId ("Bracket") is filed under ProjectId ("Rocket Build").
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            // Omitting projectId must leave the assignment intact.
            await client.CallToolAsync("update_print", new Dictionary<string, object>
            {
                ["id"] = McpTestData.ProjectPrintId,
                ["notes"] = "touch",
            });
            Assert.Equal(McpTestData.ProjectId, ProjectIdOf(McpTestData.ProjectPrintId));

            // Naming projectId in 'clear' must remove it.
            await client.CallToolAsync("update_print", new Dictionary<string, object>
            {
                ["id"] = McpTestData.ProjectPrintId,
                ["clear"] = new[] { "projectId" },
            });
            Assert.Null(ProjectIdOf(McpTestData.ProjectPrintId));
        }

        private Guid? ProjectIdOf(long printId)
        {
            using var scope = _factory.Services.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            return ctx.Prints.Where(p => p.Id == printId).Select(p => p.ProjectId).First();
        }
    }
}
