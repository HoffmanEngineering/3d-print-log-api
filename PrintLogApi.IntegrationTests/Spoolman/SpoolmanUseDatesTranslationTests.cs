using Microsoft.EntityFrameworkCore;
using PrintLogApi.Services.Spoolman;
using Xunit;

namespace PrintLogApi.IntegrationTests.Spoolman;

/// <summary>
/// The Spoolman inventory's first and last use dates are aggregated in SQL, one row per spool
/// (#309). They used to read every usage row the user had and group in memory, which was most of
/// a 1.9 s p95 on a cache miss. Compiled through the SQL Server provider, as
/// <c>UsageRowSqlServerTranslationTests</c> does, because SQLite runs the suite and production
/// runs SQL Server; <c>SpoolmanApiTests.FirstAndLastUse_SpanEveryPrintOfTheSpool</c> runs it.
/// </summary>
public class SpoolmanUseDatesTranslationTests
{
    private static PrintLogContext SqlServerContext() =>
        new(new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlServer("Server=unused;Database=unused;Trusted_Connection=True;")
            .Options);

    [Fact]
    public void UseDates_AreGroupedPerSpoolInSql()
    {
        using var context = SqlServerContext();

        var sql = SpoolmanInventoryService.UseDatesQuery(context, 42).ToQueryString();

        // Aliases are EF's to choose; the columns and aggregates are what matter.
        Assert.Matches(@"GROUP BY \[\w+\]\.\[FilamentId\]", sql);
        // SQL Server compares datetimeoffset by its UTC instant, which is what the in-memory
        // version compared, so the earliest and latest starts are the same rows.
        Assert.Matches(@"MIN\(\[\w+\]\.\[StartDate\]\)", sql);
        Assert.Matches(@"MAX\(\[\w+\]\.\[StartDate\]\)", sql);
        // An undated print counts at its creation time, and only an undated one.
        Assert.Matches(@"MIN\(CASE\s+WHEN \[\w+\]\.\[StartDate\] IS NULL THEN \[\w+\]\.\[CreatedDate\]", sql);
        Assert.Matches(@"MAX\(CASE\s+WHEN \[\w+\]\.\[StartDate\] IS NULL THEN \[\w+\]\.\[CreatedDate\]", sql);
        // One row per spool, not one per usage row: nothing outside the aggregates but the key.
        Assert.DoesNotMatch(@"SELECT\s+\[\w+\]\.\[StartDate\]", sql);
    }
}
