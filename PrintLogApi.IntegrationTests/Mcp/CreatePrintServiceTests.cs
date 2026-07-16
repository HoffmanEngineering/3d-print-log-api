using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.Mcp;
using PrintLogApi.Models;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    /// <summary>Direct service-level tests for CreatePrintForMcp: creation, idempotent replay, ownership.</summary>
    public class CreatePrintServiceTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private readonly McpDataWebApplicationFactory _factory;

        public CreatePrintServiceTests(McpDataWebApplicationFactory factory) => _factory = factory;

        [Fact]
        public async Task CreatePrintForMcp_CreatesPrint_AndIsIdempotent()
        {
            using var scope = _factory.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IPrintService>();
            var userId = IntegrationTestSeeder.TestUserId;
            var printerId = McpTestData.SearchPrinterId; // primary user's printer
            var usage = new List<MaterialUsageInput>
            {
                new(IntegrationTestSeeder.TestFilamentId1, McpMeasurementSource.Weight, 18.0, null, null, null),
            };

            var first = await svc.CreatePrintForMcp(userId, "Benchy", printerId, Print.PrintStatus.Success,
                DateTimeOffset.UtcNow, 3600, "note", null, usage, "svc-key-1", CancellationToken.None);
            var second = await svc.CreatePrintForMcp(userId, "Benchy", printerId, Print.PrintStatus.Success,
                DateTimeOffset.UtcNow, 3600, "note", null, usage, "svc-key-1", CancellationToken.None);

            Assert.False(first.WasReplayed);
            Assert.True(second.WasReplayed);
            Assert.Equal(first.Print.Id, second.Print.Id);
            Assert.Contains(first.MaterialRemaining, m => m.MaterialId == IntegrationTestSeeder.TestFilamentId1);
        }

        [Fact]
        public async Task CreatePrintForMcp_ForeignPrinter_Throws()
        {
            using var scope = _factory.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IPrintService>();

            await Assert.ThrowsAsync<McpToolException>(() => svc.CreatePrintForMcp(
                IntegrationTestSeeder.TestUserId, "x", McpTestData.OtherPrinterId, Print.PrintStatus.Success,
                null, null, null, null, new List<MaterialUsageInput>(), "svc-key-foreign", CancellationToken.None));
        }
    }
}
