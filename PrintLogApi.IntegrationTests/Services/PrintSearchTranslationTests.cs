using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// Pins the SQL Server translation of the search predicate. No database is touched:
/// ToQueryString compiles the query through the SQL Server provider, which is the only way this
/// suite can see the production SQL at all — the test host is SQLite, and SQLite translates
/// .Contains() to instr(...) rather than LIKE.
///
/// Two of these assertions guard traps that compile and run happily when wrong:
///   * ESCAPE — EF.Functions.Like omits it, silently making `50%` a wildcard search.
///   * Collation placement — Collate(col).ToLower() emits LOWER(col COLLATE ...), which folds
///     case with a different collation's table than intended.
/// </summary>
public class PrintSearchTranslationTests
{
    private static string SqlFor(Expression<Func<Print, bool>> predicate)
    {
        using var context = new PrintLogContext(new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlServer("Server=unused;Database=unused;Trusted_Connection=True;")
            .Options);

        return context.Prints.Where(predicate).Select(p => p.Id).ToQueryString();
    }

    [Fact]
    public void TitleOrNotes_LowersOutsideTheCollation()
    {
        var sql = SqlFor(PrintSearchPredicate.TitleOrNotes("wall"));

        Assert.Contains("LOWER([p].[Title]) COLLATE Latin1_General_BIN2", sql, StringComparison.Ordinal);
        Assert.Contains("LOWER([p].[Notes]) COLLATE Latin1_General_BIN2", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TitleOrNotes_KeepsAnEscapeClauseOnEveryComparison()
    {
        var sql = SqlFor(PrintSearchPredicate.TitleOrNotes("wall"));

        // Counted, not merely present. A mutation leaving ONE field on EF.Functions.Like — which
        // emits no ESCAPE — would still satisfy a bare Assert.Contains("ESCAPE").
        Assert.Equal(2, Regex.Matches(sql, "ESCAPE").Count);
        Assert.Equal(2, Regex.Matches(sql, "COLLATE Latin1_General_BIN2").Count);

        // Wrong nesting: LOWER(col COLLATE C) rather than LOWER(col) COLLATE C.
        Assert.DoesNotContain("[p].[Title] COLLATE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("[p].[Notes] COLLATE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TitleNotesOrProjectName_CollatesAllThreeColumns()
    {
        var sql = SqlFor(PrintSearchPredicate.TitleNotesOrProjectName("wall"));

        // The project alias is [p0] via a LEFT JOIN — verified against EF Core 10, not guessed.
        Assert.Contains("LOWER([p].[Title]) COLLATE Latin1_General_BIN2", sql, StringComparison.Ordinal);
        Assert.Contains("LOWER([p].[Notes]) COLLATE Latin1_General_BIN2", sql, StringComparison.Ordinal);
        Assert.Contains("LOWER([p0].[Name]) COLLATE Latin1_General_BIN2", sql, StringComparison.Ordinal);

        Assert.Equal(3, Regex.Matches(sql, "ESCAPE").Count);
        Assert.Equal(3, Regex.Matches(sql, "COLLATE Latin1_General_BIN2").Count);

        Assert.DoesNotContain("[p0].[Name] COLLATE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitCriteria_MatchesTheLegacySplittingRules()
    {
        Assert.Empty(PrintSearchPredicate.SplitCriteria(null));
        Assert.Empty(PrintSearchPredicate.SplitCriteria("   "));
        Assert.Equal(new[] { "bed", "temp" }, PrintSearchPredicate.SplitCriteria("bed temp"));
        Assert.Equal(new[] { "bed temp" }, PrintSearchPredicate.SplitCriteria("\"bed temp\""));
        Assert.Equal(new[] { "a", "bed temp", "b" }, PrintSearchPredicate.SplitCriteria("a \"bed temp\" b"));

        // An empty quoted pair yields an EMPTY criterion. That is a pre-existing quirk, not a
        // bug being introduced here, and it is pinned so the rewrite cannot change it silently:
        // an empty term becomes LIKE '%%', which matches every non-null field.
        Assert.Equal(new[] { "a", "" }, PrintSearchPredicate.SplitCriteria("a \"\""));
    }
}
