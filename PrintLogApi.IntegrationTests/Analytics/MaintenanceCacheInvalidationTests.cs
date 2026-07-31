using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.PrinterMaintenance;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Analytics
{
    public class MaintenanceCacheInvalidationTests : IClassFixture<Mcp.McpDataWebApplicationFactory>
    {
        private readonly Mcp.McpDataWebApplicationFactory _factory;

        public MaintenanceCacheInvalidationTests(Mcp.McpDataWebApplicationFactory factory) =>
            _factory = factory;

        [Fact]
        public async Task EveryMaintenanceMutation_BumpsTheOwningUsersCacheVersion()
        {
            using var scope = _factory.Services.CreateScope();
            var maintenance = scope.ServiceProvider.GetRequiredService<IPrinterMaintenanceService>();
            var cacheVersions = scope.ServiceProvider.GetRequiredService<ICacheVersionService>();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            var printerId = await db.Printers
                .Where(p => p.UserId == Mcp.McpTestData.MetricsUserId)
                .Select(p => p.Id)
                .FirstAsync();

            string Version() => cacheVersions.GetUserCacheVersion(Mcp.McpTestData.MetricsUserId);

            // A fixed date: this test is about cache versions, and a wall-clock value adds
            // nondeterminism to something that does not depend on the time at all.
            var date = new DateTimeOffset(2026, 6, 20, 12, 0, 0, TimeSpan.Zero);

            PrinterMaintenance created = null;
            try
            {
                // CREATE
                var beforeCreate = Version();
                created = await maintenance.AddEntry(
                    new AddPrinterMaintenanceDto
                    {
                        PrinterId = printerId,
                        Done = true,
                        Date = date,
                        Category = "Nozzle",
                        Description = "Cache invalidation probe",
                        PriceValue = "12.34",
                    },
                    Mcp.McpTestData.MetricsUserId);
                Assert.NotEqual(beforeCreate, Version());

                // UPDATE
                var beforeUpdate = Version();
                await maintenance.UpdateEntry(
                    created.Id,
                    new PutPrinterMaintenanceDto
                    {
                        Id = created.Id,
                        PrinterId = printerId,
                        Done = true,
                        Date = date,
                        Category = "Nozzle",
                        Description = "Cache invalidation probe (edited)",
                        PriceValue = "23.45",
                    },
                    Mcp.McpTestData.MetricsUserId);
                Assert.NotEqual(beforeUpdate, Version());

                // DELETE
                var beforeDelete = Version();
                await maintenance.DeleteMaintenanceEntry(await maintenance.GetEntryById(created.Id));
                Assert.NotEqual(beforeDelete, Version());
                created = null; // deleted by the assertion path itself
            }
            finally
            {
                // The RED state of this test — before the fix — fails on the first assertion and
                // would otherwise leave a priced maintenance row in the shared fixture database,
                // contaminating the analytics tests that run after it.
                if (created is not null)
                {
                    var stillThere = await maintenance.GetEntryById(created.Id);
                    if (stillThere is not null) await maintenance.DeleteMaintenanceEntry(stillThere);
                }
            }
        }
    }
}
