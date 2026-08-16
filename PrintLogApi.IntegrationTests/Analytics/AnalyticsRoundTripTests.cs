using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.Models.DTOs.Analytics;
using PrintLogApi.Services.Analytics;
using Xunit;

namespace PrintLogApi.IntegrationTests.Analytics
{
    /// <summary>
    /// Guards the number of database round-trips each analytics endpoint makes.
    ///
    /// These tabs each ask several questions about ONE filtered set of prints — a coverage
    /// total, an undated count, a row cap, an earliest start — and every one of those used to be
    /// its own scan. Correctness tests cannot see the difference between one aggregate and four,
    /// so without a test on the count there is nothing stopping the scans creeping back in one
    /// well-meaning edit at a time.
    ///
    /// The ceilings are deliberately a little above the current count: this is a ratchet against
    /// regression, not a lock on the exact query plan.
    /// </summary>
    public class AnalyticsRoundTripTests : IClassFixture<Mcp.McpDataWebApplicationFactory>
    {
        private readonly Mcp.McpDataWebApplicationFactory _factory;

        public AnalyticsRoundTripTests(Mcp.McpDataWebApplicationFactory factory) => _factory = factory;

        private sealed class CommandCounter : DbCommandInterceptor
        {
            public List<string> Commands { get; } = new();

            public override InterceptionResult<DbDataReader> ReaderExecuting(
                DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
            {
                Commands.Add(command.CommandText);
                return result;
            }

            public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
                DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
                CancellationToken cancellationToken = default)
            {
                Commands.Add(command.CommandText);
                return ValueTask.FromResult(result);
            }
        }

        /// <summary>
        /// A context over the SAME seeded connection the factory uses, with a counting
        /// interceptor attached. Building it by hand is what makes the interceptor possible
        /// without changing how the application registers its context.
        /// </summary>
        private (PrintLogContext Context, CommandCounter Counter) CountingContext()
        {
            using var scope = _factory.Services.CreateScope();
            var connection = scope.ServiceProvider
                .GetRequiredService<PrintLogContext>().Database.GetDbConnection();

            var counter = new CommandCounter();
            var options = new DbContextOptionsBuilder<PrintLogContext>()
                .UseSqlite(connection)
                .AddInterceptors(counter)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
                .Options;

            return (new PrintLogContext(options), counter);
        }

        private static AnalyticsFilter Ranged()
        {
            var filter = new AnalyticsFilter
            {
                TimeZone = "UTC",
                FromDate = new System.DateTimeOffset(2026, 6, 1, 0, 0, 0, System.TimeSpan.Zero),
                ToDate = new System.DateTimeOffset(2026, 7, 1, 0, 0, 0, System.TimeSpan.Zero),
            };
            filter.Normalize();
            return filter;
        }

        [Fact]
        public async Task Activity_StaysUnderItsRoundTripBudget()
        {
            var (context, counter) = CountingContext();
            using (context)
            {
                var service = new ActivityAnalyticsService(context);
                await service.GetActivity(Mcp.McpTestData.MetricsUserId, Ranged(), CancellationToken.None);
            }

            // Measured at 5. Was 8 before the aggregates were collapsed.
            Assert.True(counter.Commands.Count <= 6,
                $"Activity issued {counter.Commands.Count} queries:\n{string.Join("\n---\n", counter.Commands)}");
        }

        [Fact]
        public async Task Activity_ReadsUserSettingsOnlyOnce()
        {
            var (context, counter) = CountingContext();
            using (context)
            {
                var service = new ActivityAnalyticsService(context);
                await service.GetActivity(Mcp.McpTestData.MetricsUserId, Ranged(), CancellationToken.None);
            }

            // The response's currency and the cost projection's rate come from the same read.
            // Two reads is both a wasted round-trip and a way for one response to label figures
            // with a currency it did not price them in.
            var settingsReads = counter.Commands.FindAll(c => c.Contains("UserSettings")).Count;
            Assert.Equal(1, settingsReads);
        }

        [Fact]
        public async Task Costs_StaysUnderItsRoundTripBudget()
        {
            var (context, counter) = CountingContext();
            using (context)
            {
                var service = new CostAnalyticsService(context);
                await service.GetCosts(Mcp.McpTestData.MetricsUserId, Ranged(), CancellationToken.None);
            }

            // Measured at 6. Was 7.
            Assert.True(counter.Commands.Count <= 7,
                $"Costs issued {counter.Commands.Count} queries:\n{string.Join("\n---\n", counter.Commands)}");
        }

        [Fact]
        public async Task Materials_ReadsUserSettingsOnlyOnce()
        {
            var (context, counter) = CountingContext();
            using (context)
            {
                var service = new MaterialAnalyticsService(context);
                await service.GetMaterials(Mcp.McpTestData.MetricsUserId, Ranged(), CancellationToken.None);
            }

            var settingsReads = counter.Commands.FindAll(c => c.Contains("UserSettings")).Count;
            Assert.Equal(1, settingsReads);
        }

        [Fact]
        public async Task Overview_StaysUnderItsRoundTripBudget()
        {
            var (context, counter) = CountingContext();
            using (context)
            {
                var service = new AnalyticsService(context);
                await service.GetOverview(Mcp.McpTestData.MetricsUserId, Ranged(), CancellationToken.None);
            }

            // Overview is the widest tab: four metric sums that must stay top-level so the shared
            // PrintMetrics expressions translate, plus the status groups, the series, the cost
            // pass and three highlight queries.
            // Measured at 13, down from 15.
            Assert.True(counter.Commands.Count <= 14,
                $"Overview issued {counter.Commands.Count} queries:\n{string.Join("\n---\n", counter.Commands)}");
        }
    }
}
