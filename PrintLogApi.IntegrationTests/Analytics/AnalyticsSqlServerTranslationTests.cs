using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Services.Analytics;
using Xunit;

namespace PrintLogApi.IntegrationTests.Analytics
{
    /// <summary>
    /// Asserts that the collapsed analytics aggregates translate on the provider we actually ship.
    ///
    /// The rest of the suite runs on SQLite; production is SQL Server. That gap is normally
    /// harmless, but these aggregates lean on two constructs the two providers do NOT treat alike:
    /// GroupBy over a constant, and a correlated Count() inside a Sum(). SQL Server rejects an
    /// aggregate whose argument contains a subquery, so the naive translation of
    /// <c>Sum(p =&gt; p.FilamentUsage.Count())</c> is an error — it only works because EF lifts the
    /// correlated count into an OUTER APPLY first. Nothing in the correctness suite can see that,
    /// and the failure mode is a 500 in production on a query that passes every test.
    ///
    /// No database is involved: ToQueryString compiles the query through the SQL Server provider,
    /// which is where a translation failure surfaces. That is also why these call the production
    /// query builders rather than restating the LINQ — a copy here would drift and then guard
    /// nothing.
    /// </summary>
    public class AnalyticsSqlServerTranslationTests
    {
        private static PrintLogContext SqlServerContext() =>
            new(new DbContextOptionsBuilder<PrintLogContext>()
                .UseSqlServer("Server=unused;Database=unused;Trusted_Connection=True;")
                .Options);

        private static string Translate(Func<IQueryable<Print>, IQueryable> build)
        {
            using var context = SqlServerContext();
            return build(context.Prints.AsNoTracking()).ToQueryString();
        }

        [Fact]
        public void ScopedPrintCounts_TranslatesOnSqlServer()
        {
            var sql = Translate(AnalyticsPrintCounts.Query);

            Assert.Contains("COUNT(*)", sql);
            Assert.Contains("MIN(", sql);
        }

        [Fact]
        public void CostRowCaps_TranslateOnSqlServer()
        {
            var sql = Translate(AnalyticsCostProjection.CapsQuery);

            // The correlated filament count must reach SQL as an APPLY, not as a join into the
            // outer set: a join would multiply the print rows and inflate PrintRows alongside it.
            Assert.Contains("APPLY", sql);
            Assert.Contains("SUM(", sql);
        }

        [Fact]
        public void MaterialScopeStats_TranslateOnSqlServer()
        {
            var sql = Translate(MaterialAnalyticsService.StatsQuery);

            Assert.Contains("APPLY", sql);
            Assert.Contains("SUM(", sql);
        }
    }
}
