using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Analytics;
using PrintLogApi.Services.Analytics;
using Xunit;

namespace PrintLogApi.IntegrationTests.Analytics
{
    public class AnalyticsQueryScopeTests : IClassFixture<Mcp.McpDataWebApplicationFactory>
    {
        private readonly Mcp.McpDataWebApplicationFactory _factory;

        public AnalyticsQueryScopeTests(Mcp.McpDataWebApplicationFactory factory) => _factory = factory;

        private PrintLogContext Db(IServiceScope scope) =>
            scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        [Fact]
        public async Task Scope_ReturnsOnlyTheTenantsPrints()
        {
            using var scope = _factory.Services.CreateScope();
            var db = Db(scope);

            var scoped = AnalyticsQueryScope.Scope(
                db.Prints.AsNoTracking(), Mcp.McpTestData.MetricsUserId, new AnalyticsFilter(), null, null);

            Assert.All(await scoped.ToListAsync(),
                p => Assert.Equal(Mcp.McpTestData.MetricsUserId, p.CreatedById));
        }

        [Fact]
        public async Task Scope_AnUnownedPrinterIdMatchesNothingRatherThanThrowing()
        {
            using var scope = _factory.Services.CreateScope();
            var db = Db(scope);

            var filter = new AnalyticsFilter { PrinterIds = { long.MaxValue } };
            var scoped = AnalyticsQueryScope.Scope(
                db.Prints.AsNoTracking(), Mcp.McpTestData.MetricsUserId, filter, null, null);

            Assert.Empty(await scoped.ToListAsync());
        }

        [Fact]
        public async Task Scope_RangeIsHalfOpenSoAdjacentWindowsNeverDoubleCount()
        {
            using var scope = _factory.Services.CreateScope();
            var db = Db(scope);

            var boundary = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
            var filter = new AnalyticsFilter();

            var before = await AnalyticsQueryScope
                .Scope(db.Prints.AsNoTracking(), Mcp.McpTestData.MetricsUserId, filter,
                       boundary.AddYears(-5), boundary)
                .CountAsync();
            var after = await AnalyticsQueryScope
                .Scope(db.Prints.AsNoTracking(), Mcp.McpTestData.MetricsUserId, filter,
                       boundary, boundary.AddYears(5))
                .CountAsync();
            var whole = await AnalyticsQueryScope
                .Scope(db.Prints.AsNoTracking(), Mcp.McpTestData.MetricsUserId, filter,
                       boundary.AddYears(-5), boundary.AddYears(5))
                .CountAsync();

            Assert.Equal(whole, before + after);
        }

        [Fact]
        public async Task Scope_WithoutARangeIncludesUndatedPrints()
        {
            using var scope = _factory.Services.CreateScope();
            var db = Db(scope);

            var all = AnalyticsQueryScope.Scope(
                db.Prints.AsNoTracking(), Mcp.McpTestData.MetricsUserId, new AnalyticsFilter(), null, null);

            var ranged = AnalyticsQueryScope.Scope(
                db.Prints.AsNoTracking(), Mcp.McpTestData.MetricsUserId, new AnalyticsFilter(),
                new DateTimeOffset(1990, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero));

            Assert.True(await all.CountAsync() >= await ranged.CountAsync());
            Assert.Empty(await ranged.Where(p => p.StartDate == null).ToListAsync());
        }
    }
}
