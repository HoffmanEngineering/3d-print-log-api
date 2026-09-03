using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// Behaviour of the search predicate over a real query.
///
/// ASCII ONLY, deliberately. SQLite's LOWER() folds ASCII and nothing else, and its instr()
/// ignores collations, so non-ASCII results here would say nothing about SQL Server. The
/// SQL Server behaviour is covered two other ways: PrintSearchTranslationTests pins the
/// generated SQL, and PrintSearchCollationCorpusTests runs the differential corpus against a
/// real SQL Server when PRINTLOG_SQLSERVER_TEST_CONNECTION points at one.
/// </summary>
public class PrintSearchSemanticsTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private PrintLogContext _context = null!;

    public async ValueTask InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=False");
        await _connection.OpenAsync(TestContext.Current.CancellationToken);
        SqliteSearchCollation.Register(_connection);

        _context = new PrintLogContext(new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlite(_connection).Options);
        await _context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        _context.Prints.AddRange(
            new Print { Id = 1, CreatedById = 1, PrinterId = 1, Title = "Outer Wall Speed", Notes = "nothing here" },
            new Print { Id = 2, CreatedById = 1, PrinterId = 1, Title = "Benchy", Notes = "outer_wall_speed=45 and brim" },
            new Print { Id = 3, CreatedById = 1, PrinterId = 1, Title = "Calibration", Notes = "bed_temp=60" },
            new Print { Id = 4, CreatedById = 1, PrinterId = 1, Title = "Draft", Notes = null },
            // Null Title with matching Notes: the OR must not be short-circuited by a null title.
            new Print { Id = 5, CreatedById = 1, PrinterId = 1, Title = null, Notes = "wall mounted bracket" },
            // Both null: must never match, not even an empty criterion.
            new Print { Id = 6, CreatedById = 1, PrinterId = 1, Title = null, Notes = null },
            // Negative control for the underscore test: matches ONLY if `_` acts as a wildcard.
            new Print { Id = 7, CreatedById = 1, PrinterId = 1, Title = "Control", Notes = "bedXtemp=60" },
            // A second tenant, to prove search composes with ownership rather than replacing it.
            new Print { Id = 8, CreatedById = 2, PrinterId = 1, Title = "Outer Wall Speed", Notes = "other user" });
        await _context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    /// <summary>
    /// Mirrors how PrintService composes these: the tenant filter first, then one Where per term.
    /// Pass userId: null to search across tenants (only the ownership test should do that).
    /// </summary>
    private async Task<List<long>> Search(string searchText, long? userId = 1)
    {
        IQueryable<Print> query = _context.Prints;

        if (userId is { } id)
        {
            query = query.Where(p => p.CreatedById == id);
        }

        foreach (var term in PrintSearchPredicate.SplitCriteria(searchText))
        {
            query = query.Where(PrintSearchPredicate.TitleOrNotes(term));
        }

        return await query.OrderBy(p => p.Id).Select(p => p.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MatchesRegardlessOfCase()
    {
        Assert.Equal(new long[] { 1, 2, 5 }, await Search("WALL"));
        Assert.Equal(new long[] { 1, 2, 5 }, await Search("wall"));
    }

    [Fact]
    public async Task MatchesInsideAToken()
    {
        // The reason full-text search was rejected: this must keep working.
        Assert.Equal(new long[] { 2 }, await Search("wall_speed"));
    }

    [Fact]
    public async Task MultipleTermsAreAndedAcrossTitleAndNotes()
    {
        Assert.Equal(new long[] { 2 }, await Search("benchy brim"));
        Assert.Empty(await Search("benchy calibration"));
    }

    [Fact]
    public async Task QuotedPhraseIsOneTerm()
    {
        Assert.Empty(await Search("\"speed outer\""));
        Assert.Equal(new long[] { 1 }, await Search("\"outer wall\""));
    }

    [Fact]
    public async Task NullNotesDoesNotMatchAndDoesNotThrow()
    {
        Assert.DoesNotContain(4L, await Search("draft brim"));
        Assert.Equal(new long[] { 4 }, await Search("draft"));
    }

    [Fact]
    public async Task NullTitleStillMatchesOnNotes()
    {
        // The other direction of the OR. A null-title guard wrapped around the whole predicate
        // would hide this row while every other test stayed green.
        Assert.Equal(new long[] { 5 }, await Search("mounted bracket"));
    }

    [Fact]
    public async Task UnderscoreIsALiteralNotAWildcard()
    {
        // Row 7 is "bedXtemp=60". It matches ONLY if `_` is treated as a wildcard, so this
        // assertion fails the moment the ESCAPE clause is lost.
        Assert.Equal(new long[] { 3 }, await Search("bed_temp"));
    }

    [Fact]
    public async Task EmptyQuotedTermMatchesRowsWithAnyNonNullField()
    {
        // Pre-existing quirk, pinned rather than fixed: `""` splits to [""], and the empty
        // criterion becomes LIKE '%%'. Row 4 (null Notes) and row 5 (null Title) still match on
        // their other field; row 6 has both null and must NOT match. Changing this is a
        // caller-visible decision, not a refactor.
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 7 }, await Search("\"\""));
    }

    [Fact]
    public async Task SearchComposesWithOwnershipRatherThanReplacingIt()
    {
        // Row 8 belongs to user 2 and has a matching title. Wiring that applied the search
        // before, or instead of, the tenant filter would leak it.
        Assert.DoesNotContain(8L, await Search("wall", userId: 1));
        Assert.Equal(new long[] { 8 }, await Search("wall", userId: 2));
        Assert.Equal(new long[] { 1, 2, 5, 8 }, await Search("wall", userId: null));
    }
}
