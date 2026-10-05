using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PrintLogApi.Achievements;
using Xunit;

namespace PrintLogApi.IntegrationTests.Achievements;

/// <summary>
/// Guardrails on the code-defined catalog. Keys are permanent once shipped, and any change to a
/// key's thresholds must bump <see cref="AchievementCatalog.Version"/> so existing users get a
/// catch-up pass.
/// </summary>
public partial class AchievementCatalogTests
{
    // SHA-256 over the sorted "key:t1,t2,..." lines of catalog Version 1.
    private const string ExpectedHashForVersion1 = "C1027BA0FDD9CB46BF044B78678EF19C9905E1B1424CF0B66AB06B7E14A6A06B";

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex KebabCase();

    [Fact]
    public void Keys_AreUniqueKebabCase_MaxLength64()
    {
        var keys = AchievementCatalog.Definitions.Select(d => d.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, k =>
        {
            Assert.Matches(KebabCase(), k);
            Assert.InRange(k.Length, 1, 64);
        });
    }

    [Fact]
    public void Thresholds_StrictlyAscending_OneTimeIsExactlyOne()
    {
        Assert.All(AchievementCatalog.Definitions, d =>
        {
            Assert.NotEmpty(d.Thresholds);
            Assert.InRange(d.Thresholds.Count, 1, AchievementCatalog.TierNames.Count);
            for (var i = 1; i < d.Thresholds.Count; i++)
            {
                Assert.True(d.Thresholds[i] > d.Thresholds[i - 1], $"{d.Key} thresholds must strictly ascend");
            }

            if (d.Thresholds.Count == 1)
            {
                Assert.Equal(1, d.Thresholds[0]);
            }
        });
    }

    [Fact]
    public void HintOrder_UniqueAndOnlyOnGettingStarted()
    {
        var hinted = AchievementCatalog.Definitions.Where(d => d.HintOrder > 0).ToList();

        Assert.All(hinted, d => Assert.Equal(AchievementCategory.GettingStarted, d.Category));
        Assert.Equal(hinted.Count, hinted.Select(d => d.HintOrder).Distinct().Count());
        Assert.All(
            AchievementCatalog.Definitions.Where(d => d.Category == AchievementCategory.GettingStarted),
            d => Assert.True(d.HintOrder > 0, $"{d.Key} needs a HintOrder"));
    }

    [Fact]
    public void Definitions_Count_Is34() => Assert.Equal(34, AchievementCatalog.Definitions.Count);

    [Fact]
    public void TotalTierCount_Is90() => Assert.Equal(90, AchievementCatalog.TotalTierCount);

    [Fact]
    public void TierNames_AreTheFilamentFinishes()
    {
        Assert.Equal(
            ["Bronze PLA", "Silver PLA", "Gold Silk", "Rainbow Silk", "Carbon Fiber", "Glow-in-the-Dark"],
            AchievementCatalog.TierNames);
    }

    [Fact]
    public void ShippedKeys_AreAllPresent()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Achievements", "ShippedAchievementKeys.txt");
        var shipped = File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        Assert.NotEmpty(shipped);
        Assert.All(shipped, key => Assert.True(
            AchievementCatalog.Find(key) is not null,
            $"Shipped key '{key}' is gone. Keys can be retired (Retired = true) but never deleted or renamed."));
    }

    [Fact]
    public void CatalogHash_MatchesVersion()
    {
        var lines = AchievementCatalog.Definitions
            .Select(d => $"{d.Key}:{string.Join(',', d.Thresholds)}")
            .OrderBy(l => l, StringComparer.Ordinal);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));

        Assert.True(
            AchievementCatalog.Version == 1 && hash == ExpectedHashForVersion1,
            $"The catalog's keys or thresholds changed (hash {hash}): bump AchievementCatalog.Version and update the hash");
    }

    [Fact]
    public void Glyphs_AreKnown()
    {
        Assert.All(AchievementCatalog.Definitions, d => Assert.True(
            AchievementGlyphs.IsKnown(d.Glyph), $"{d.Key} uses unknown glyph '{d.Glyph}'"));
    }

    [Fact]
    public void Templates_UseOnlyKnownPlaceholders()
    {
        Assert.All(AchievementCatalog.Definitions, d =>
        {
            foreach (var template in new[] { d.DescriptionTemplate, d.UnlockedTemplate })
            {
                var stripped = template.Replace("{n}", "", StringComparison.Ordinal).Replace("{s}", "", StringComparison.Ordinal);
                Assert.DoesNotContain('{', stripped);
            }

            if (d.Thresholds.Count > 1)
            {
                Assert.Contains("{n}", d.DescriptionTemplate, StringComparison.Ordinal);
            }
        });
    }

    [Fact]
    public void Format_Pluralizes()
    {
        Assert.Equal("1 project", AchievementCopy.Format("{n} project{s}", 1));
        Assert.Equal("25 projects", AchievementCopy.Format("{n} project{s}", 25));
        Assert.Equal("1,000 hours", AchievementCopy.Format("{n} hours", 1000));
    }
}
