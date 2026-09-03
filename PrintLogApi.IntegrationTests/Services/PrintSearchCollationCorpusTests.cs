using Microsoft.Data.SqlClient;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// The differential corpus from the spec, as an executable test.
///
/// Skipped unless PRINTLOG_SQLSERVER_TEST_CONNECTION is set: this measures SQL Server collation
/// behaviour, which the SQLite test host cannot reproduce at all. It touches no table — every
/// case is evaluated as a scalar SELECT — so it is safe against any database.
///
/// Wildcard rows are deliberately absent. This test is about COLLATION; escaping is pinned by
/// PrintSearchTranslationTests (ESCAPE occurrence counts) and by
/// PrintSearchSemanticsTests.UnderscoreIsALiteralNotAWildcard.
/// </summary>
public class PrintSearchCollationCorpusTests
{
    private static string? Connection =>
        Environment.GetEnvironmentVariable("PRINTLOG_SQLSERVER_TEST_CONNECTION");

    /// <summary>label, data, term, expected agreement between old and new predicate.</summary>
    public static TheoryData<string, string, string, bool> Corpus() => new()
    {
        { "ascii case",            "Outer Wall Speed",  "wall",        true  },
        { "ascii case upper term", "outer wall speed",  "WALL",        true  },
        { "snake_case mid-token",  "outer_wall_speed",  "wall",        true  },
        { "accent vs plain",       "café",         "cafe",        true  },
        { "plain vs accent term",  "cafe",              "café",   true  },
        { "sharp-s vs ss",         "Straße",       "ss",          false },
        { "ss vs sharp-s term",    "Strasse",           "ß",      false },
        { "fullwidth data",        "ＡＢ",      "AB",          false },
        { "halfwidth term",        "AB",                "ＡＢ", false },
        { "hiragana vs katakana",  "あい",      "アイ", false },
        { "katakana vs hiragana",  "アイ",      "あい", false },
        { "turkish dotted I",      "İstanbul",     "i",           false },
        { "turkish dotless i",     "ı",            "I",           true  },
        { "greek final sigma",     "σ",            "ς",      false },
        { "greek case",            "Σ",            "σ",      true  },
        { "cyrillic case",         "Д",            "д",      true  },
        { "precomposed e-acute",   "é",            "é",     false },
        { "decomposed e-acute",    "é",           "é",      false },
        { "ligature fi",           "ﬁlament",      "fi",          false },
        { "surrogate pair",        "\U0001F60A",        "\U0001F60A",  true  },
        { "ndash vs hyphen",       "PLA–HT",       "PLA-HT",      true  },
        { "trailing space term",   "bed temp",          "bed ",        true  },
        { "empty term",            "anything",          "",            true  },
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task OldAndNewPredicateAgree(string label, string data, string term, bool expectedAgreement)
    {
        if (string.IsNullOrWhiteSpace(Connection))
        {
            Assert.Skip("Set PRINTLOG_SQLSERVER_TEST_CONNECTION to run the collation corpus.");
        }

        await using var connection = new SqlConnection(Connection);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              CAST(CASE WHEN @data LIKE N'%' + @term + N'%' THEN 1 ELSE 0 END AS bit),
              CAST(CASE WHEN LOWER(@data) COLLATE Latin1_General_BIN2
                             LIKE N'%' + LOWER(@term) COLLATE Latin1_General_BIN2 + N'%'
                        THEN 1 ELSE 0 END AS bit)
            """;
        command.Parameters.Add(new SqlParameter("@data", System.Data.SqlDbType.NVarChar, 200) { Value = data });
        command.Parameters.Add(new SqlParameter("@term", System.Data.SqlDbType.NVarChar, 60) { Value = term });

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));

        var oldMatched = reader.GetBoolean(0);
        var newMatched = reader.GetBoolean(1);

        // Assert.True rather than Assert.Equal so the label reaches the failure message — a bare
        // "expected False, actual True" says nothing about which of 23 cases moved.
        Assert.True(
            expectedAgreement == (oldMatched == newMatched),
            $"{label}: CI_AS matched {oldMatched}, BIN2 matched {newMatched}; "
            + $"expected the two to agree: {expectedAgreement}.");
    }
}
