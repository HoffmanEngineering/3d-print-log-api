using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi;
using PrintLogApi.Models.DTOs.Analytics;
using PrintLogApi.Services.Analytics;
using Xunit;

namespace PrintLogApi.IntegrationTests.Analytics
{
    public class AnalyticsCostProjectionTests : IClassFixture<Mcp.McpDataWebApplicationFactory>
    {
        private readonly Mcp.McpDataWebApplicationFactory _factory;

        public AnalyticsCostProjectionTests(Mcp.McpDataWebApplicationFactory factory) => _factory = factory;

        [Fact]
        public async Task Project_ReturnsOneRowPerScopedPrintAndNeverLeavesTheTenant()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            var scoped = AnalyticsQueryScope.Scope(
                db.Prints.AsNoTracking(), Mcp.McpTestData.MetricsUserId, new AnalyticsFilter(), null, null);

            var projection = await AnalyticsCostProjection.Project(
                db, Mcp.McpTestData.MetricsUserId, scoped, CancellationToken.None);

            Assert.False(projection.RowCapExceeded);
            Assert.Equal(await scoped.CountAsync(), projection.Prints.Count);
            Assert.Equal(projection.PrintCount, projection.Prints.Count);

            var ownedIds = await scoped.Select(p => p.Id).ToListAsync();
            Assert.All(projection.Prints, p => Assert.Contains(p.PrintId, ownedIds));
        }

        [Fact]
        public async Task Project_TotalIsFilamentPlusElectricityAndStaysNullWhenNeitherIsKnown()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            var scoped = AnalyticsQueryScope.Scope(
                db.Prints.AsNoTracking(), Mcp.McpTestData.MetricsUserId, new AnalyticsFilter(), null, null);

            var projection = await AnalyticsCostProjection.Project(
                db, Mcp.McpTestData.MetricsUserId, scoped, CancellationToken.None);

            foreach (var print in projection.Prints)
            {
                if (print.FilamentCost is null && print.ElectricityCost is null)
                {
                    // A print with no knowable cost must not report a confident 0.00.
                    Assert.Null(print.Total);
                }
                else
                {
                    Assert.Equal((print.FilamentCost ?? 0m) + (print.ElectricityCost ?? 0m), print.Total);
                }
            }
        }

        [Fact]
        public async Task Project_ReadsNoFilamentOrPrinterBelongingToAnotherUser()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            // Attach a foreign spool to one of the tenant's prints. The projection must behave
            // exactly as if that row were absent — an unowned join is a cross-tenant read even
            // when the parent print is correctly scoped.
            var foreign = await db.Filaments.AsNoTracking()
                .FirstOrDefaultAsync(f => f.CreatedById != Mcp.McpTestData.MetricsUserId);
            Assert.NotNull(foreign); // the seeder must provide a second user's spool

            var print = await db.Prints
                .FirstAsync(p => p.CreatedById == Mcp.McpTestData.MetricsUserId);

            async Task<decimal?> CostOfThePrint()
            {
                var scoped = AnalyticsQueryScope.Scope(
                    db.Prints.AsNoTracking(), Mcp.McpTestData.MetricsUserId,
                    new AnalyticsFilter(), null, null);
                var projection = await AnalyticsCostProjection.Project(
                    db, Mcp.McpTestData.MetricsUserId, scoped, CancellationToken.None);
                return projection.Prints.Single(p => p.PrintId == print.Id).FilamentCost;
            }

            var before = await CostOfThePrint();

            var row = new PrintLogApi.Models.PrintFilament
            {
                PrintId = print.Id,
                FilamentId = foreign.Id,
                AmountMg = 50_000,
                Source = PrintLogApi.Models.PrintFilament.SourceMeasurement.Weight,
            };
            db.Set<PrintLogApi.Models.PrintFilament>().Add(row);
            await db.SaveChangesAsync();

            try
            {
                // Identical before and after: the foreign spool contributed nothing. Without the
                // ownership predicate this assertion fails, because 50 g of someone else's
                // filament would be priced into this user's total.
                Assert.Equal(before, await CostOfThePrint());
            }
            finally
            {
                db.Remove(row);
                await db.SaveChangesAsync();
            }
        }

        [Fact]
        public async Task Project_ReadsNoPrinterWattageBelongingToAnotherUser()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            // The ownership guard on Printer.WattageW has its own path and its own way of
            // failing: a foreign printer's wattage would silently price this user's electricity.
            var print = await db.Prints
                .FirstAsync(p => p.CreatedById == Mcp.McpTestData.MetricsUserId);
            var originalPrinterId = print.PrinterId;

            var foreignPrinter = await db.Printers.AsNoTracking()
                .FirstOrDefaultAsync(p => p.UserId != Mcp.McpTestData.MetricsUserId && p.WattageW > 0);
            Assert.NotNull(foreignPrinter); // the seeder must provide another user's printer

            async Task<decimal?> ElectricityOfThePrint()
            {
                var scoped = AnalyticsQueryScope.Scope(
                    db.Prints.AsNoTracking(), Mcp.McpTestData.MetricsUserId,
                    new AnalyticsFilter(), null, null);
                var projection = await AnalyticsCostProjection.Project(
                    db, Mcp.McpTestData.MetricsUserId, scoped, CancellationToken.None);
                return projection.Prints.Single(p => p.PrintId == print.Id).ElectricityCost;
            }

            print.PrinterId = foreignPrinter.Id;
            await db.SaveChangesAsync();

            try
            {
                // The foreign wattage must be invisible: costing falls back to the user's own
                // default wattage setting, exactly as an unset wattage would.
                var withForeignPrinter = await ElectricityOfThePrint();
                var expected = PrintCostCalculator.ElectricityCost(
                    print.PrintTimeInSeconds ?? print.EstimatedPrintTimeInSeconds ?? 0,
                    null,
                    await AnalyticsCostProjection.LoadInputs(
                        db, Mcp.McpTestData.MetricsUserId, CancellationToken.None)).Amount;

                Assert.Equal(expected, withForeignPrinter);
            }
            finally
            {
                print.PrinterId = originalPrinterId;
                await db.SaveChangesAsync();
            }
        }

        [Fact]
        public async Task Project_SumOfTotalsMatchesWhatTheOverviewEndpointReports()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var analytics = scope.ServiceProvider.GetRequiredService<IAnalyticsService>();

            var filter = new AnalyticsFilter();
            filter.Normalize();

            var overview = await analytics.GetOverview(
                Mcp.McpTestData.MetricsUserId, filter, CancellationToken.None);

            var scoped = AnalyticsQueryScope.Scope(
                db.Prints.AsNoTracking(), Mcp.McpTestData.MetricsUserId, filter, null, null);
            var projection = await AnalyticsCostProjection.Project(
                db, Mcp.McpTestData.MetricsUserId, scoped, CancellationToken.None);

            var summed = projection.Prints.Any(p => p.Total.HasValue)
                ? projection.Prints.Sum(p => p.Total ?? 0m)
                : (decimal?)null;

            Assert.Equal(overview.Tiles.TotalCost.Value, summed);
        }
    }
}
