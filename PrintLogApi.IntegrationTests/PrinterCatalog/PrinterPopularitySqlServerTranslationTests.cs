using Microsoft.EntityFrameworkCore;
using PrintLogApi.PrinterCatalog;
using Xunit;

namespace PrintLogApi.IntegrationTests.PrinterCatalog;

/// <summary>
/// SQLite runs the suite and SQL Server serves production, so this compiles the popularity query
/// through the SQL Server provider: <c>ToQueryString</c> throws when part of it would run on the
/// client. No database is involved.
/// </summary>
public class PrinterPopularitySqlServerTranslationTests
{
    [Fact]
    public void OwnerCounts_TranslatesToOneGroupedQuery_WithNoClientEvaluation()
    {
        using var context = new PrintLogContext(new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlServer("Server=unused;Database=unused;Trusted_Connection=True;")
            .Options);

        var sql = PrinterPopularityJob.OwnerCounts(context, new DateTimeOffset(2025, 10, 18, 0, 0, 0, TimeSpan.Zero)).ToQueryString();

        Assert.Contains("GROUP BY", sql, StringComparison.Ordinal);
        Assert.Contains("COUNT(DISTINCT", sql, StringComparison.Ordinal);
        Assert.Contains("EXISTS", sql, StringComparison.Ordinal);
    }
}
