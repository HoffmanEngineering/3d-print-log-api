using Microsoft.EntityFrameworkCore;
using PrintLogApi.Mcp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp;

/// <summary>
/// Pure normalization behaviour. Fast, but note these alone would pass even with a malformed
/// expression tree — see <see cref="McpTextMatchTranslationTests"/> for the part that matters.
/// </summary>
public class McpTextMatchTests
{
    [Theory]
    // Real production spellings. Non-matches are as load-bearing as matches.
    [InlineData("PLA", "PLA", true)]
    [InlineData("PLA", "PLA (Polylactic Acid)", true)]
    [InlineData("PLA", "PLA+", true)]
    [InlineData("PLA", "Silk PLA", true)]
    [InlineData("PLA", "PLA-CF", true)]
    [InlineData("PLA", "Wood PLA", true)]
    [InlineData("PLA", "LW-PLA", true)]
    [InlineData("Polylactic Acid", "PLA (Polylactic Acid)", true)]
    [InlineData("TPU", "TPU 95A", true)]
    [InlineData("PC", "PCTG", false)]
    [InlineData("PLA", "PETG (Polyethylene Terephthalate Glycol)", false)]
    [InlineData("PLA", "ABS (Acrylonitrile Butadiene Styrene)", false)]
    // Multi-token queries across differing separator runs. These fail without space collapsing.
    [InlineData("PLA-CF", "PLA--CF", true)]
    [InlineData("PLA CF", "PLA + - CF", true)]
    [InlineData("Light Blue", "Light--Blue", true)]
    [InlineData("Light Blue", "Light  Blue", true)]
    // Colors
    [InlineData("blue", "Blue", true)]
    [InlineData("blue", "Light Blue", true)]
    [InlineData("green", "Olive Green", true)]
    [InlineData("green", "Blue", false)]
    public void Matches_RealWorldSpellings(string query, string stored, bool expected)
    {
        var haystack = " " + McpTextMatch.Normalize(stored) + " ";
        var needle = " " + McpTextMatch.Normalize(query) + " ";

        Assert.Equal(expected, haystack.Contains(needle));
    }

    [Fact]
    public void Normalize_CollapsesLongSeparatorRuns()
    {
        // A run of 100 separators must collapse to a single space. Four passes would leave
        // seven spaces here and silently break multi-token matching.
        var stored = "PLA" + new string('-', 100) + "CF";

        Assert.Equal("pla cf", McpTextMatch.Normalize(stored));
    }

    [Theory]
    [InlineData("+")]
    [InlineData("---")]
    [InlineData("()")]
    [InlineData("")]
    [InlineData("   ")]
    public void RequireFilter_NormalizingToNothing_Throws(string input)
    {
        // Such a filter would pad to " " and match EVERY row. It must be rejected, never
        // treated as "no filter".
        Assert.Throws<McpToolException>(() => McpTextMatch.RequireFilter(input, "material"));
    }

    [Fact]
    public void RequireFilter_TooLong_Throws() =>
        Assert.Throws<McpToolException>(
            () => McpTextMatch.RequireFilter(new string('a', McpTextMatch.MaxFilterLength + 1), "material"));

    [Fact]
    public void RequireFilter_Valid_ReturnsNormalized() =>
        Assert.Equal("pla cf", McpTextMatch.RequireFilter("  PLA--CF ", "material"));
}

/// <summary>
/// Executes the predicates against the real (SQLite) provider. EF Core throws on
/// untranslatable expressions rather than evaluating them on the client, so these tests are
/// what actually prove the expression tree is valid SQL.
/// </summary>
public class McpTextMatchTranslationTests : IClassFixture<McpDataWebApplicationFactory>
{
    private readonly McpDataWebApplicationFactory factory;

    public McpTextMatchTranslationTests(McpDataWebApplicationFactory factory) => this.factory = factory;

    [Fact]
    public async Task MaterialMatches_TranslatesToSql_AndFindsVariants()
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        // If the expression tree is malformed, EF throws here. That throw is the assertion.
        var materials = await context.Filaments.AsNoTracking()
            .Where(McpTextMatch.MaterialMatches("PLA"))
            .Select(f => f.MaterialType)
            .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("PLA", materials);
        Assert.Contains("PLA (Polylactic Acid)", materials);
        Assert.Contains("PLA+", materials);
        Assert.DoesNotContain(materials, m => m!.StartsWith("PETG"));
    }

    [Fact]
    public async Task MaterialMatches_ShortAcronym_DoesNotSubstringMatch()
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var materials = await context.Filaments.AsNoTracking()
            .Where(McpTextMatch.MaterialMatches("PC"))
            .Select(f => f.MaterialType)
            .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("PCTG", materials);
    }

    [Fact]
    public async Task ColorMatches_TranslatesToSql_AndFindsQualifiedColors()
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var colors = await context.Filaments.AsNoTracking()
            .Where(McpTextMatch.ColorMatches("blue"))
            .Select(f => f.ColorName)
            .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("Blue", colors);
        Assert.Contains("Light Blue", colors);
        Assert.DoesNotContain("Navy", colors);
    }
}
