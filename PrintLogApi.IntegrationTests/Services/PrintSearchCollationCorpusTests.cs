using System.Text;
using Microsoft.Data.SqlClient;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// The differential corpus from the spec, as an executable test.
///
/// Skipped unless PRINTLOG_SQLSERVER_TEST_CONNECTION is set: this measures SQL Server collation
/// behaviour, which the SQLite test host cannot reproduce at all — its LOWER() folds ASCII only
/// and its instr() ignores collations. It touches no table (every case is a scalar SELECT) so it
/// is safe against any database, and it names SQL_Latin1_General_CP1_CI_AS explicitly rather than
/// relying on the target database's default, so a run against a differently-collated server still
/// measures the production comparison.
///
/// Two design points, both the result of review:
///
///   * <b>Each case asserts BOTH results, not merely that they agree.</b> An agreement-only
///     assertion passes when the behavioural direction reverses — a "differs" case stays green
///     if the new predicate starts matching where it is meant to stop.
///   * <b>The term is lowered in .NET, not in SQL.</b> That is what production does:
///     .Contains() parameterises the term, so only the COLUMN reaches SQL Server's LOWER().
///     Lowering both sides in SQL would hide the U+0130 rows below, which are the one place the
///     two runtimes' case tables disagree.
///
/// Wildcard rows are deliberately absent. This is about COLLATION; escaping is pinned by
/// PrintSearchTranslationTests (ESCAPE occurrence counts) and by
/// PrintSearchSemanticsTests.UnderscoreIsALiteralNotAWildcard.
/// </summary>
public class PrintSearchCollationCorpusTests
{
    private static string? Connection =>
        Environment.GetEnvironmentVariable("PRINTLOG_SQLSERVER_TEST_CONNECTION");

    /// <summary>The collation of Prints.Title and Prints.Notes in production.</summary>
    private const string ColumnCollation = "SQL_Latin1_General_CP1_CI_AS";

    /// <summary>One corpus case: the text, the search term, and how each predicate behaves.</summary>
    private sealed record Case(string Label, string Data, string Term, bool CiAsMatches, bool BinaryMatches);

    /// <summary>
    /// The corpus itself. Held as data rather than built straight into TheoryData so that
    /// NormalisationRowsAreActuallyDistinct can inspect it without going through xUnit.
    /// </summary>
    private static readonly Case[] Cases =
    {
        // ── Unchanged: the cases real searches are made of ───────────────────────────────────
        new("ascii case",            "Outer Wall Speed",  "wall",        true,  true),
        new("ascii case upper term", "outer wall speed",  "WALL",        true,  true),
        new("snake_case mid-token",  "outer_wall_speed",  "wall",        true,  true),
        new("accent vs plain",       "café",              "cafe",        false, false),
        new("plain vs accent term",  "cafe",              "café",        false, false),
        new("greek case",            "Σ",                 "σ",           true,  true),
        new("cyrillic case",         "Д",                 "д",           true,  true),
        new("surrogate pair",        "\U0001F60A",        "\U0001F60A",  true,  true),
        new("ndash vs hyphen",       "PLA–HT",            "PLA-HT",      false, false),
        new("trailing space term",   "bed temp",          "bed ",        true,  true),
        new("empty term",            "anything",          "",            true,  true),

        // ── Narrowed: CI_AS matched, binary does not ─────────────────────────────────────────
        // Only the sharp-s pair is reachable in production data; the census found none of the
        // rest. See the spec for the character-frequency evidence.
        new("sharp-s vs ss",         "Straße",            "ss",          true,  false),
        new("ss vs sharp-s term",    "Strasse",           "ß",           true,  false),
        new("fullwidth data",        "ＡＢ",               "AB",          true,  false),
        new("halfwidth term",        "AB",                "ＡＢ",         true,  false),
        new("hiragana vs katakana",  "あい",               "アイ",         true,  false),
        new("katakana vs hiragana",  "アイ",               "あい",         true,  false),
        new("greek final sigma",     "σ",                 "ς",           true,  false),
        // These two rows are the ONLY place the file's own Unicode normalisation is load-bearing:
        // one holds precomposed U+00E9, the other "e" + combining U+0301. An editor or tool that
        // normalised the file would collapse them into two identical rows that still pass and
        // prove nothing, so NormalisationRowsAreActuallyDistinct guards them without needing a
        // database.
        new("precomposed e-acute",   "é",            "é",     true,  false),
        new("decomposed e-acute",    "é",           "é",      true,  false),
        new("ligature fi",           "ﬁlament",           "fi",          true,  false),

        // ── The U+0130 pair, which moves in BOTH directions ──────────────────────────────────
        // SQL Server's LOWER folds Turkish dotted capital I to "i"; .NET's ToLowerInvariant
        // leaves it alone (measured, not assumed). Because production lowers the column in SQL
        // and the term in .NET, the two rows below move opposite ways — searching "i" now
        // matches where it did not, and searching the capital itself no longer does.
        new("dotted I in data",      "İstanbul",          "i",           false, true),
        new("dotted I in term",      "İstanbul",          "İ",           true,  false),
        new("turkish dotless i",     "ı",                 "I",           false, false),
    };

    public static TheoryData<string, string, string, bool, bool> Corpus()
    {
        var data = new TheoryData<string, string, string, bool, bool>();
        foreach (var c in Cases)
        {
            data.Add(c.Label, c.Data, c.Term, c.CiAsMatches, c.BinaryMatches);
        }

        return data;
    }

    /// <summary>
    /// Runs with no database, so the corpus cannot rot silently while the gated theory is skipped.
    /// The NFC/NFD pair is the one content in this file that a normalising tool would destroy
    /// while leaving every assertion green.
    /// </summary>
    [Fact]
    public void NormalisationRowsAreActuallyDistinct()
    {
        var precomposed = Cases.Single(c => c.Label == "precomposed e-acute");
        var decomposed = Cases.Single(c => c.Label == "decomposed e-acute");

        Assert.NotEqual(precomposed.Data, precomposed.Term, StringComparer.Ordinal);
        Assert.NotEqual(decomposed.Data, decomposed.Term, StringComparer.Ordinal);

        // Distinct as code points, identical as text: exactly the mismatch binary comparison
        // cannot see through, and the reason the production census counted combining marks.
        Assert.Equal(
            precomposed.Data.Normalize(NormalizationForm.FormC),
            decomposed.Data.Normalize(NormalizationForm.FormC),
            StringComparer.Ordinal);
        Assert.Contains('́', decomposed.Data);
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task PredicatesBehaveAsMeasured(
        string label, string data, string term, bool ciAsMatches, bool binaryMatches)
    {
        if (string.IsNullOrWhiteSpace(Connection))
        {
            Assert.Skip("Set PRINTLOG_SQLSERVER_TEST_CONNECTION to run the collation corpus.");
        }

        await using var connection = new SqlConnection(Connection);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
              CAST(CASE WHEN @data COLLATE {ColumnCollation}
                             LIKE N'%' + @term + N'%'
                        THEN 1 ELSE 0 END AS bit),
              CAST(CASE WHEN LOWER(@data COLLATE {ColumnCollation}) COLLATE {PrintSearchPredicate.BinaryCollation}
                             LIKE N'%' + @needle COLLATE {PrintSearchPredicate.BinaryCollation} + N'%'
                        THEN 1 ELSE 0 END AS bit)
            """;
        command.Parameters.Add(new SqlParameter("@data", System.Data.SqlDbType.NVarChar, 200) { Value = data });
        command.Parameters.Add(new SqlParameter("@term", System.Data.SqlDbType.NVarChar, 60) { Value = term });
        // Lowered here, exactly as PrintSearchPredicate does, because .Contains() parameterises
        // the term and it therefore never reaches SQL Server's case table.
        command.Parameters.Add(new SqlParameter("@needle", System.Data.SqlDbType.NVarChar, 60)
        {
            Value = term.ToLowerInvariant()
        });

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));

        var actualCiAs = reader.GetBoolean(0);
        var actualBinary = reader.GetBoolean(1);

        // Assert.True rather than Assert.Equal so the label reaches the failure message — a bare
        // "expected False, actual True" says nothing about which of 24 cases moved.
        Assert.True(
            ciAsMatches == actualCiAs && binaryMatches == actualBinary,
            $"{label}: expected CI_AS={ciAsMatches}/binary={binaryMatches}, "
            + $"got CI_AS={actualCiAs}/binary={actualBinary}.");
    }
}
