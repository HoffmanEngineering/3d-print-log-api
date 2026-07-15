using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.Models;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    /// <summary>
    /// Cross-cutting hardening for the write surface: authorization visibility, confused-deputy
    /// isolation, cache invalidation, idempotency-after-deletion, and boundary validation.
    /// </summary>
    public class McpWriteHardeningTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private readonly McpDataWebApplicationFactory _factory;

        public McpWriteHardeningTests(McpDataWebApplicationFactory factory) => _factory = factory;

        private static readonly string[] ReadWrite = { "read:printdata", "write:printdata" };
        private static readonly string[] ReadOnly = { "read:printdata" };

        [Fact]
        public async Task WriteToken_SeesWriteTools_ReadOnlyDoesNot()
        {
            await using var writeClient = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
            var writeNames = (await writeClient.ListToolsAsync()).Select(t => t.Name).ToHashSet();
            Assert.Contains("log_print", writeNames);
            Assert.Contains("add_material", writeNames);

            await using var readClient = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadOnly);
            var readNames = (await readClient.ListToolsAsync()).Select(t => t.Name).ToHashSet();
            Assert.DoesNotContain("log_print", readNames);
            Assert.DoesNotContain("add_material", readNames);
        }

        [Fact]
        public async Task LogPrint_OwnPrinter_ForeignProject_ReturnsNotFound()
        {
            // Confused deputy: the caller owns the printer but references another user's project.
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            var code = await McpDataWebApplicationFactory.ToolErrorCode(client, "log_print",
                new Dictionary<string, object>
                {
                    ["title"] = "deputy",
                    ["printerId"] = McpTestData.SearchPrinterId,     // caller's printer
                    ["status"] = "Success",
                    ["projectId"] = McpTestData.ForeignProjectId,     // another user's project
                    ["idempotencyKey"] = "harden-deputy",
                });

            Assert.Equal("not_found", code);
        }

        [Fact]
        public async Task LogPrint_BumpsUserCacheVersion()
        {
            var cache = _factory.Services.GetRequiredService<ICacheVersionService>();
            var before = cache.GetUserCacheVersion(IntegrationTestSeeder.TestUserId);

            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
            var result = await client.CallToolAsync("log_print", new Dictionary<string, object>
            {
                ["title"] = "cache-bump",
                ["printerId"] = McpTestData.SearchPrinterId,
                ["status"] = "Success",
                ["idempotencyKey"] = "harden-cache",
            });
            Assert.True(result.IsError != true);

            var after = cache.GetUserCacheVersion(IntegrationTestSeeder.TestUserId);
            Assert.NotEqual(before, after);
        }

        [Fact]
        public async Task LogPrint_ReplayAfterDeletion_ReturnsNotFound()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            var first = await client.CallToolAsync("log_print", new Dictionary<string, object>
            {
                ["title"] = "to-delete",
                ["printerId"] = McpTestData.SearchPrinterId,
                ["status"] = "Success",
                ["idempotencyKey"] = "harden-replay-del",
            });
            Assert.True(first.IsError != true);

            // Delete the created print out from under the idempotency record.
            using (var scope = _factory.Services.CreateScope())
            {
                var ctx = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                var toDelete = ctx.Prints.Where(p => p.Title == "to-delete").ToList();
                ctx.Prints.RemoveRange(toDelete);
                ctx.SaveChanges();
            }

            // Replaying the key after its print was deleted must be rejected, never silently re-created.
            var isError = await McpDataWebApplicationFactory.IsToolError(client, "log_print",
                new Dictionary<string, object>
                {
                    ["title"] = "to-delete",
                    ["printerId"] = McpTestData.SearchPrinterId,
                    ["status"] = "Success",
                    ["idempotencyKey"] = "harden-replay-del",
                });

            Assert.True(isError);
        }

        [Fact]
        public async Task LogPrint_ZeroDuration_IsRejected()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            var isError = await McpDataWebApplicationFactory.IsToolError(client, "log_print",
                new Dictionary<string, object>
                {
                    ["title"] = "zero-dur",
                    ["printerId"] = McpTestData.SearchPrinterId,
                    ["status"] = "Success",
                    ["durationSeconds"] = 0,
                    ["idempotencyKey"] = "harden-zero-dur",
                });

            Assert.True(isError);
        }

        [Fact]
        public async Task LogPrint_DuplicateMaterial_IsRejected()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            var row = new Dictionary<string, object>
            {
                ["materialId"] = IntegrationTestSeeder.TestFilamentId1,
                ["source"] = "Weight",
                ["amount"] = 5.0,
            };
            var isError = await McpDataWebApplicationFactory.IsToolError(client, "log_print",
                new Dictionary<string, object>
                {
                    ["title"] = "dupe-mat",
                    ["printerId"] = McpTestData.SearchPrinterId,
                    ["status"] = "Success",
                    ["idempotencyKey"] = "harden-dupe",
                    ["materials"] = new[] { row, row },
                });

            Assert.True(isError);
        }
    }
}
